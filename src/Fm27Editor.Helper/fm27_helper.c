/*
 * fm27_helper.c - on-demand privileged Mach-VM helper for FM27 Editor.
 *
 * launchd starts this process only when a client connects to the socket named
 * "Fm27Socket" (SockPathName=/var/run/fm27editor.sock). It performs the Mach VM
 * calls the unprivileged editor is not allowed to make (task_for_pid,
 * mach_vm_read/write/allocate/map/protect), then exits after a short idle
 * period so launchd can start it again on the next connection.
 *
 * Protocol (all integers little-endian):
 *   request  = uint32 payload_length, then payload
 *   payload  = uint8 opcode, then opcode arguments
 *   response = uint32 payload_length, then payload
 *   payload  = int32 status (0 ok, 1 error), then data
 *   on error: int32 code, uint32 message_length, message bytes
 *
 * Opcodes:
 *   1 Open(pid:i32, writable:u8)            -> handle:i32
 *   2 Read(handle:i32, addr:u64, len:u32)   -> data bytes
 *   3 Write(handle:i32, addr:u64, len:u32, data)
 *   4 Allocate(handle:i32, bytes:u32)       -> addr:u64
 *   5 AllocateNear(handle:i32, near:u64, bytes:u32) -> addr:u64
 *   6 Protect(handle:i32, addr:u64, bytes:u32, vm_prot:u32, set_max:u8) -> old_page:u32
 *   7 Free(handle:i32, addr:u64, bytes:u64)
 *   8 Enumerate(handle:i32, start:u64, end:u64) -> count:u32, records
 *   9 Close(handle:i32)
 *  10 DyldInfo(handle:i32)                  -> info_addr:u64, info_size:u64
 *  11 Pause(handle:i32)                     -> token:u32
 *  12 Resume(handle:i32)
 *  13 ReadMany(handle:i32, count:u32, [addr:u64, len:u32]) -> count:u32, [status:i32, len:u32, data]
 *  14 RemoteCalloc(handle:i32, bytes:u32)   -> addr:u64
 *     calloc(1, bytes) inside the target, so the game can later free() the block with its own allocator.
 *     A raw Mach thread runs a small stub that starts a pthread (pthread_create_from_mach_thread); the pthread
 *     calls calloc and stores the result. calloc and pthread_create_from_mach_thread are resolved here and used at
 *     the same address in the target (one dyld shared cache per boot); their first bytes are compared first.
 *
 * Built with -DFM27_PLUGIN the same file is the in-game plugin, libfm27editor_plugin.dylib. The game loads it through
 * DYLD_INSERT_LIBRARIES (its entitlements allow DYLD environment variables and unsigned libraries), so it reaches the
 * game's memory from inside and needs neither root nor task_for_pid on another process, which System Integrity
 * Protection refuses for the game's Hardened Runtime. It serves the same protocol on $TMPDIR/fm27editor-<pid>.sock
 * (owner-only), opens only its own process, calls calloc directly and never suspends its own thread.
 */

#include <dlfcn.h>
#include <errno.h>
#include <launch.h>
#include <mach/mach.h>
#include <mach/mach_vm.h>
#include <mach/vm_statistics.h>
#include <poll.h>
#include <pthread.h>
#include <signal.h>
#include <stdarg.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/socket.h>
#include <sys/stat.h>
#include <sys/un.h>
#include <time.h>
#include <unistd.h>

#define MAX_TASKS 32
#define MAX_MESSAGE (256u * 1024u * 1024u)
#define IDLE_SECONDS 5
#define SUBMAP_MAX_DEPTH 16
#define ALLOC_GRANULARITY 0x4000ULL
#define BRANCH_REACH 0x08000000ULL
#define MAX_SCATTER_READS 1024u
#define MAX_SCATTER_BYTES 32u

/* VM_PROT_READ/WRITE/EXECUTE come from <mach/vm_prot.h>. */
#define VM_PROT_RW (VM_PROT_READ | VM_PROT_WRITE)
#define VM_PROT_RWX (VM_PROT_READ | VM_PROT_WRITE | VM_PROT_EXECUTE)

/* Windows PAGE_* values the editor's code-patch presets use. */
#define PAGE_READONLY 0x02
#define PAGE_READWRITE 0x04
#define PAGE_EXECUTE_READ 0x20

enum
{
    OP_OPEN = 1,
    OP_READ = 2,
    OP_WRITE = 3,
    OP_ALLOCATE = 4,
    OP_ALLOCATE_NEAR = 5,
    OP_PROTECT = 6,
    OP_FREE = 7,
    OP_ENUMERATE = 8,
    OP_CLOSE = 9,
    OP_DYLD_INFO = 10,
    OP_PAUSE = 11,
    OP_RESUME = 12,
    OP_READ_MANY = 13,
    OP_REMOTE_CALLOC = 14,
    OP_HEAP_CHECK = 15,
    OP_SCAN_VTABLES = 16
};

typedef struct
{
    uint8_t *data;
    size_t len;
    size_t cap;
} buffer;

typedef struct
{
    int used;
    int owner;
    mach_port_t task;
    pid_t pid;
    int paused;
    thread_act_array_t threads;
    mach_msg_type_number_t thread_count;
    uint64_t calloc_data;      /* read/write block: function pointers, arguments, result and flags */
    uint64_t calloc_code;      /* read/execute copy of k_calloc_stub */
    uint64_t calloc_stack_top; /* top of the Mach thread's stack */
} task_slot;

typedef struct
{
    uint64_t start;
    uint64_t end;
} interval;

typedef struct
{
    const uint8_t *p;
    const uint8_t *end;
} reader;

static task_slot g_tasks[MAX_TASKS];

/* ------------------------------------------------------------------ buffer */

static int buf_reserve(buffer *b, size_t extra)
{
    if (extra > MAX_MESSAGE)
        return -1;
    if (b->len + extra <= b->cap)
        return 0;
    size_t cap = b->cap ? b->cap : 256;
    while (cap < b->len + extra)
    {
        if (cap > MAX_MESSAGE)
            return -1;
        cap *= 2;
    }
    uint8_t *p = realloc(b->data, cap);
    if (p == NULL)
        return -1;
    b->data = p;
    b->cap = cap;
    return 0;
}

static int buf_u8(buffer *b, uint8_t v)
{
    if (buf_reserve(b, 1) != 0)
        return -1;
    b->data[b->len++] = v;
    return 0;
}

static int buf_u32(buffer *b, uint32_t v)
{
    if (buf_reserve(b, 4) != 0)
        return -1;
    memcpy(b->data + b->len, &v, 4);
    b->len += 4;
    return 0;
}

static int buf_u64(buffer *b, uint64_t v)
{
    if (buf_reserve(b, 8) != 0)
        return -1;
    memcpy(b->data + b->len, &v, 8);
    b->len += 8;
    return 0;
}

static int buf_bytes(buffer *b, const void *data, size_t len)
{
    if (len > MAX_MESSAGE || buf_reserve(b, len) != 0)
        return -1;
    if (len > 0)
        memcpy(b->data + b->len, data, len);
    b->len += len;
    return 0;
}

/* ------------------------------------------------------------------ reader */

static int rd_u8(reader *r, uint8_t *v)
{
    if (r->p + 1 > r->end)
        return 0;
    *v = *r->p++;
    return 1;
}

static int rd_u32(reader *r, uint32_t *v)
{
    if (r->p + 4 > r->end)
        return 0;
    memcpy(v, r->p, 4);
    r->p += 4;
    return 1;
}

static int rd_u64(reader *r, uint64_t *v)
{
    if (r->p + 8 > r->end)
        return 0;
    memcpy(v, r->p, 8);
    r->p += 8;
    return 1;
}

static int rd_i32(reader *r, int32_t *v)
{
    uint32_t u;
    if (!rd_u32(r, &u))
        return 0;
    *v = (int32_t)u;
    return 1;
}

/* -------------------------------------------------------------- socket I/O */

static int write_exact(int fd, const void *data, size_t len)
{
    const uint8_t *p = data;
    while (len > 0)
    {
        ssize_t n = write(fd, p, len);
        if (n < 0)
        {
            if (errno == EINTR)
                continue;
            return -1;
        }
        if (n == 0)
            return -1;
        p += (size_t)n;
        len -= (size_t)n;
    }
    return 0;
}

static int read_exact(int fd, void *data, size_t len)
{
    uint8_t *p = data;
    while (len > 0)
    {
        ssize_t n = read(fd, p, len);
        if (n < 0)
        {
            if (errno == EINTR)
                continue;
            return -1;
        }
        if (n == 0)
            return -1;
        p += (size_t)n;
        len -= (size_t)n;
    }
    return 0;
}

static int send_frame(int fd, const void *data, uint32_t len)
{
    uint32_t n = len;
    if (write_exact(fd, &n, 4) != 0)
        return -1;
    if (len > 0 && write_exact(fd, data, len) != 0)
        return -1;
    return 0;
}

static void send_error(int fd, int32_t code, const char *message)
{
    buffer b = {0};
    uint32_t len = message ? (uint32_t)strlen(message) : 0;
    if (buf_u32(&b, 1) != 0 || buf_u32(&b, (uint32_t)code) != 0 ||
        buf_u32(&b, len) != 0 || buf_bytes(&b, message, len) != 0)
    {
        free(b.data);
        return;
    }
    send_frame(fd, b.data, (uint32_t)b.len);
    free(b.data);
}

static void send_errorf(int fd, int32_t code, const char *format, ...)
{
    char message[512];
    va_list args;
    va_start(args, format);
    vsnprintf(message, sizeof(message), format, args);
    va_end(args);
    send_error(fd, code, message);
}

/* ------------------------------------------------------------- task slots */

static task_slot *slot_for(int32_t handle)
{
    if (handle < 1 || handle > MAX_TASKS)
        return NULL;
    task_slot *s = &g_tasks[handle - 1];
    return s->used ? s : NULL;
}

static void resume_task(task_slot *s)
{
    if (!s->paused)
        return;
    for (mach_msg_type_number_t i = 0; i < s->thread_count; i++)
    {
        thread_resume(s->threads[i]);
        mach_port_deallocate(mach_task_self(), s->threads[i]);
    }
    if (s->threads != NULL)
        mach_vm_deallocate(mach_task_self(), (mach_vm_address_t)s->threads,
                           (mach_vm_size_t)s->thread_count * sizeof(thread_act_t));
    s->threads = NULL;
    s->thread_count = 0;
    s->paused = 0;
}

static void close_slot(task_slot *s)
{
    if (!s->used)
        return;
    resume_task(s);
    if (s->task != MACH_PORT_NULL)
        mach_port_deallocate(mach_task_self(), s->task);
    *s = (task_slot){0};
}

static void close_client_tasks(int client_id)
{
    for (int i = 0; i < MAX_TASKS; i++)
        if (g_tasks[i].used && g_tasks[i].owner == client_id)
            close_slot(&g_tasks[i]);
}

/* ------------------------------------------------------------ Mach regions */

static int query_region_info(task_t task, mach_vm_address_t *address, mach_vm_size_t *size,
                             int *protection, int *is_submap, uint32_t *user_tag, natural_t *depth)
{
    vm_region_submap_info_data_64_t info;
    mach_msg_type_number_t count = VM_REGION_SUBMAP_INFO_COUNT_64;
    memset(&info, 0, sizeof(info));
    kern_return_t kr = mach_vm_region_recurse(task, address, size, depth,
                                              (vm_region_recurse_info_t)&info, &count);
    if (kr != KERN_SUCCESS || *size == 0)
        return 0;
    *protection = (int)info.protection;
    *is_submap = info.is_submap ? 1 : 0;
    if (user_tag != NULL)
        *user_tag = info.user_tag;
    return 1;
}

static int query_region(task_t task, mach_vm_address_t *address, mach_vm_size_t *size,
                        int *protection, int *is_submap, natural_t *depth)
{
    return query_region_info(task, address, size, protection, is_submap, NULL, depth);
}

static int query_address_tag(task_t task, uint64_t address, uint32_t *user_tag,
                             uint64_t *region_start, uint64_t *region_size)
{
    mach_vm_address_t addr = (mach_vm_address_t)address;
    natural_t depth = 1;
    for (int step = 0; step < SUBMAP_MAX_DEPTH + 4; step++)
    {
        mach_vm_size_t size = 0;
        int prot = 0;
        int sub = 0;
        uint32_t tag = 0;
        if (!query_region_info(task, &addr, &size, &prot, &sub, &tag, &depth))
            return 0;
        if (sub)
        {
            if (depth >= SUBMAP_MAX_DEPTH)
                return 0;
            depth++;
            continue;
        }
        uint64_t s = (uint64_t)addr;
        uint64_t sz = (uint64_t)size;
        if (address < s || address >= s + sz)
            return 0;
        *user_tag = tag;
        *region_start = s;
        *region_size = sz;
        return 1;
    }
    return 0;
}

static int current_protection(task_t task, uint64_t address, int *protection)
{
    mach_vm_address_t addr = 0;
    natural_t depth = 1;
    for (;;)
    {
        mach_vm_size_t size = 0;
        int prot = 0;
        int sub = 0;
        if (!query_region(task, &addr, &size, &prot, &sub, &depth))
            return 0;
        if (sub)
        {
            if (depth >= SUBMAP_MAX_DEPTH)
                return 0;
            depth++;
            continue;
        }
        if (address >= (uint64_t)addr && address < (uint64_t)addr + (uint64_t)size)
        {
            *protection = prot;
            return 1;
        }
        uint64_t next = (uint64_t)addr + (uint64_t)size;
        if (next <= (uint64_t)addr)
            return 0;
        addr = (mach_vm_address_t)next;
        depth = 1;
    }
}

static uint32_t from_vm_protection(int protection)
{
    if ((protection & VM_PROT_EXECUTE) != 0)
        return PAGE_EXECUTE_READ;
    if ((protection & VM_PROT_WRITE) != 0)
        return PAGE_READWRITE;
    if (protection == 0)
        return PAGE_EXECUTE_READ;
    return PAGE_READONLY;
}

static int collect_writable(task_t task, uint64_t image_start, uint64_t image_end, buffer *records)
{
    mach_vm_address_t address = 0;
    natural_t depth = 1;
    for (;;)
    {
        mach_vm_size_t size = 0;
        int prot = 0;
        int sub = 0;
        if (!query_region(task, &address, &size, &prot, &sub, &depth))
            break;
        if (sub)
        {
            if (depth >= SUBMAP_MAX_DEPTH)
                break;
            depth++;
            continue;
        }
        if ((prot & VM_PROT_WRITE) != 0)
        {
            uint64_t start = (uint64_t)address;
            uint8_t module = (start >= image_start && start < image_end) ? 1 : 0;
            if (buf_u64(records, start) != 0 || buf_u64(records, (uint64_t)size) != 0 ||
                buf_u8(records, module) != 0)
                return -1;
        }
        uint64_t next = (uint64_t)address + (uint64_t)size;
        if (next <= (uint64_t)address)
            break;
        address = (mach_vm_address_t)next;
        depth = 1;
    }
    return 0;
}

static int collect_mapped(task_t task, uint64_t low, uint64_t high, interval **out, size_t *out_count)
{
    size_t cap = 64;
    size_t n = 0;
    interval *ranges = malloc(cap * sizeof(interval));
    if (ranges == NULL)
        return -1;

    mach_vm_address_t address = (mach_vm_address_t)low;
    natural_t depth = 1;
    for (;;)
    {
        mach_vm_size_t size = 0;
        int prot = 0;
        int sub = 0;
        if (!query_region(task, &address, &size, &prot, &sub, &depth))
            break;
        if (sub)
        {
            if (depth >= SUBMAP_MAX_DEPTH)
                break;
            depth++;
            continue;
        }
        uint64_t start = (uint64_t)address;
        uint64_t end = start + (uint64_t)size;
        if (end <= start)
            break;
        if (start < high && end > low)
        {
            if (start < low)
                start = low;
            if (end > high)
                end = high;
            if (n == cap)
            {
                size_t new_cap = cap * 2;
                interval *grown = realloc(ranges, new_cap * sizeof(interval));
                if (grown == NULL)
                {
                    free(ranges);
                    return -1;
                }
                ranges = grown;
                cap = new_cap;
            }
            ranges[n].start = start;
            ranges[n].end = end;
            n++;
        }
        address = (mach_vm_address_t)end;
        depth = 1;
        if (end >= high)
            break;
    }
    *out = ranges;
    *out_count = n;
    return 0;
}

static int try_map_at(task_t task, uint64_t at, uint64_t size, uint64_t *result)
{
    mach_vm_address_t address = (mach_vm_address_t)at;
    kern_return_t kr = mach_vm_map(task, &address, (mach_vm_size_t)size, 0, VM_FLAGS_FIXED,
                                   MEMORY_OBJECT_NULL, 0, FALSE, VM_PROT_RW,
                                   VM_PROT_RWX, VM_INHERIT_DEFAULT);
    if (kr != KERN_SUCCESS)
        return 0;
    *result = (uint64_t)address;
    return 1;
}

static int allocate_near(task_t task, uint64_t near, uint64_t bytes, uint64_t *result)
{
    int ok = 0;
    uint64_t size = (bytes + ALLOC_GRANULARITY - 1) & ~(ALLOC_GRANULARITY - 1);
    uint64_t low = near > BRANCH_REACH ? near - BRANCH_REACH : ALLOC_GRANULARITY;
    low = (low + ALLOC_GRANULARITY - 1) & ~(ALLOC_GRANULARITY - 1);
    uint64_t high = near + BRANCH_REACH;

    interval *ranges = NULL;
    size_t range_count = 0;
    if (collect_mapped(task, low, high, &ranges, &range_count) != 0)
        return 0;

    uint64_t *holes = NULL;
    size_t hole_count = 0;
    size_t hole_cap = 0;

    uint64_t cursor = low;
    for (size_t i = 0; i < range_count; i++)
    {
        uint64_t candidate = (cursor + ALLOC_GRANULARITY - 1) & ~(ALLOC_GRANULARITY - 1);
        if (ranges[i].start > candidate && ranges[i].start - candidate >= size)
        {
            if (hole_count == hole_cap)
            {
                size_t new_cap = hole_cap ? hole_cap * 2 : 16;
                uint64_t *grown = realloc(holes, new_cap * sizeof(uint64_t));
                if (grown == NULL)
                    goto done;
                holes = grown;
                hole_cap = new_cap;
            }
            holes[hole_count++] = candidate;
        }
        if (ranges[i].end > cursor)
            cursor = ranges[i].end;
    }
    uint64_t tail = (cursor + ALLOC_GRANULARITY - 1) & ~(ALLOC_GRANULARITY - 1);
    if (high > tail && high - tail >= size)
    {
        if (hole_count == hole_cap)
        {
            size_t new_cap = hole_cap ? hole_cap * 2 : 16;
            uint64_t *grown = realloc(holes, new_cap * sizeof(uint64_t));
            if (grown == NULL)
                goto done;
            holes = grown;
            hole_cap = new_cap;
        }
        holes[hole_count++] = tail;
    }
    free(ranges);
    ranges = NULL;

    /* Nearest hole to the branch site first. */
    for (size_t i = 1; i < hole_count; i++)
    {
        uint64_t value = holes[i];
        size_t j = i;
        while (j > 0)
        {
            uint64_t prev = holes[j - 1];
            uint64_t dv = value > near ? value - near : near - value;
            uint64_t dp = prev > near ? prev - near : near - prev;
            if (dp <= dv)
                break;
            holes[j] = prev;
            j--;
        }
        holes[j] = value;
    }

    for (size_t i = 0; i < hole_count; i++)
        if (try_map_at(task, holes[i], size, result))
        {
            ok = 1;
            goto done;
        }

    /* Nothing free within reach of a branch: take any hole and check the distance. */
    {
        mach_vm_address_t anywhere = 0;
        kern_return_t kr = mach_vm_map(task, &anywhere, (mach_vm_size_t)size, 0, VM_FLAGS_ANYWHERE,
                                       MEMORY_OBJECT_NULL, 0, FALSE, VM_PROT_RW,
                                       VM_PROT_RWX, VM_INHERIT_DEFAULT);
        if (kr == KERN_SUCCESS)
        {
            uint64_t at = (uint64_t)anywhere;
            int in_reach = at >= near
                ? (at + size - near) <= BRANCH_REACH
                : (near - at) <= BRANCH_REACH;
            if (in_reach)
            {
                *result = at;
                ok = 1;
                goto done;
            }
            mach_vm_deallocate(task, anywhere, (mach_vm_size_t)size);
        }
    }

done:
    free(holes);
    free(ranges);
    return ok;
}

/* -------------------------------------------------------------- operations */

static void op_open(int fd, int client_id, reader *r)
{
    int32_t pid;
    uint8_t writable;
    if (!rd_i32(r, &pid) || !rd_u8(r, &writable))
    {
        send_error(fd, 1, "malformed Open request");
        return;
    }
    (void)writable;
#ifdef FM27_PLUGIN
    if (pid != getpid())
    {
        send_error(fd, 2, "the plugin serves only the game process it is loaded in");
        return;
    }
#endif

    mach_port_t task = MACH_PORT_NULL;
    kern_return_t kr = task_for_pid(mach_task_self(), pid, &task);
    if (kr != KERN_SUCCESS || task == MACH_PORT_NULL)
    {
        send_errorf(fd, 2, "task_for_pid(%d) failed with kern_return_t %d", (int)pid, (int)kr);
        return;
    }

    int slot = -1;
    for (int i = 0; i < MAX_TASKS; i++)
        if (!g_tasks[i].used)
        {
            slot = i;
            break;
        }
    if (slot < 0)
    {
        mach_port_deallocate(mach_task_self(), task);
        send_error(fd, 3, "too many open tasks");
        return;
    }

    g_tasks[slot] = (task_slot){0};
    g_tasks[slot].used = 1;
    g_tasks[slot].owner = client_id;
    g_tasks[slot].task = task;
    g_tasks[slot].pid = pid;

    buffer out = {0};
    if (buf_u32(&out, 0) != 0 || buf_u32(&out, (uint32_t)(slot + 1)) != 0)
    {
        free(out.data);
        send_error(fd, 4, "out of memory");
        return;
    }
    send_frame(fd, out.data, (uint32_t)out.len);
    free(out.data);
}

static void op_read(int fd, reader *r)
{
    int32_t handle;
    uint64_t address;
    uint32_t length;
    if (!rd_i32(r, &handle) || !rd_u64(r, &address) || !rd_u32(r, &length))
    {
        send_error(fd, 1, "malformed Read request");
        return;
    }
    task_slot *slot = slot_for(handle);
    if (slot == NULL)
    {
        send_error(fd, 5, "invalid task handle");
        return;
    }

    if (length == 0)
    {
        uint32_t status = 0;
        send_frame(fd, &status, 4);
        return;
    }
    if (length > MAX_MESSAGE - 4)
    {
        send_error(fd, 6, "read is too large");
        return;
    }

    uint8_t small[4 + 4096];
    uint8_t *buf = length <= 4096 ? small : malloc((size_t)length + 4);
    if (buf == NULL)
    {
        send_error(fd, 4, "out of memory");
        return;
    }
    memset(buf, 0, 4);
    mach_vm_size_t got = 0;
    kern_return_t kr = mach_vm_read_overwrite(slot->task, (mach_vm_address_t)address,
                                              (mach_vm_size_t)length, (mach_vm_address_t)(buf + 4), &got);
    if (kr != KERN_SUCCESS || got != (mach_vm_size_t)length)
    {
        if (buf != small)
            free(buf);
        send_errorf(fd, 7, "mach_vm_read_overwrite(0x%llx, %u) failed with kern_return_t %d",
                    (unsigned long long)address, (unsigned)length, (int)kr);
        return;
    }
    send_frame(fd, buf, length + 4);
    if (buf != small)
        free(buf);
}

#define MAX_SCAN_VTABLES 32u
#define MAX_SCAN_REGIONS 262144u
#define SCAN_CHUNK_BYTES (4u * 1024u * 1024u)
#define SCAN_WORK_BYTES (8u * 1024u * 1024u)
#define SCAN_MAX_THREADS 8u

typedef struct
{
    uint64_t addr;
    uint64_t vt;
} scan_match;

typedef struct
{
    scan_match *items;
    size_t count;
    size_t cap;
} match_list;

typedef struct
{
    uint64_t start;
    uint64_t end;
    uint32_t user_tag;
    uint8_t has_data;
} vm_scan_meta;

typedef struct
{
    uint32_t region_idx;
    uint64_t base;
    uint64_t length;
    int is_region_start;
} scan_work_item;

typedef struct
{
    task_t task;
    const uint64_t *vtables;
    uint32_t vtable_count;
    uint64_t min_vt;
    uint64_t vt_span;
    int tolerate;
    int skip_unreadable;
    size_t page_sz;
    const scan_work_item *items;
    size_t item_count;
    match_list *results;
    uint8_t *region_skip;
    size_t next_item;
    int failed;
    pthread_mutex_t err_lock;
    int err_code;
    char err_msg[256];
} scan_ctx;

static int match_push(match_list *list, uint64_t addr, uint64_t vt)
{
    if (list->count == list->cap)
    {
        size_t new_cap = list->cap ? list->cap * 2 : 64;
        if (new_cap > (size_t)MAX_MESSAGE / sizeof(scan_match))
            return -1;
        scan_match *grown = realloc(list->items, new_cap * sizeof(scan_match));
        if (grown == NULL)
            return -1;
        list->items = grown;
        list->cap = new_cap;
    }
    list->items[list->count++] = (scan_match){addr, vt};
    return 0;
}

static int scan_words(match_list *list, uint64_t base_addr,
                      const uint8_t *bytes, size_t len,
                      const uint64_t *vtables, uint32_t vtable_count,
                      uint64_t min_vt, uint64_t vt_span)
{
    size_t word_count = len / 8;
    const uint64_t *words = (const uint64_t *)(const void *)bytes;
    if (vtable_count == 1)
    {
        uint64_t v0 = vtables[0];
        for (size_t w = 0; w < word_count; w++)
        {
            if (words[w] == v0)
            {
                if (match_push(list, base_addr + (uint64_t)w * 8, v0) != 0)
                    return -1;
            }
        }
        return 0;
    }
    for (size_t w = 0; w < word_count; w++)
    {
        uint64_t val = words[w];
        if (__builtin_expect((val - min_vt) <= vt_span, 0))
        {
            for (uint32_t k = 0; k < vtable_count; k++)
            {
                if (val == vtables[k])
                {
                    if (match_push(list, base_addr + (uint64_t)w * 8, val) != 0)
                        return -1;
                    break;
                }
            }
        }
    }
    return 0;
}

static int is_non_heap_tag(uint32_t tag)
{
    switch (tag)
    {
    case VM_MEMORY_IOKIT:
    case VM_MEMORY_LAYERKIT:
    case VM_MEMORY_CGIMAGE:
    case VM_MEMORY_COREUI:
    case VM_MEMORY_COREUIFILE:
    case VM_MEMORY_IOSURFACE:
    case VM_MEMORY_IOACCELERATOR:
    case VM_MEMORY_COREUI_CACHED_IMAGE_DATA:
        return 1;
    default:
        return 0;
    }
}

static int collect_vm_scan_meta(task_t task, uint64_t min_addr, uint64_t max_addr,
                                vm_scan_meta **out_items, size_t *out_count)
{
    size_t cap = 256, count = 0;
    vm_scan_meta *items = malloc(cap * sizeof(vm_scan_meta));
    if (items == NULL)
        return -1;

    mach_vm_address_t addr = (mach_vm_address_t)min_addr;
    natural_t depth = 1;
    while ((uint64_t)addr < max_addr)
    {
        mach_vm_size_t size = 0;
        vm_region_submap_info_data_64_t info;
        mach_msg_type_number_t info_count = VM_REGION_SUBMAP_INFO_COUNT_64;
        memset(&info, 0, sizeof(info));
        kern_return_t kr = mach_vm_region_recurse(task, &addr, &size, &depth,
                                                  (vm_region_recurse_info_t)&info, &info_count);
        if (kr != KERN_SUCCESS || size == 0)
            break;
        if (info.is_submap)
        {
            if (depth >= SUBMAP_MAX_DEPTH)
                break;
            depth++;
            continue;
        }
        uint64_t start = (uint64_t)addr;
        uint64_t end = start + (uint64_t)size;
        if (start >= max_addr || end <= start)
            break;
        if (count == cap)
        {
            size_t new_cap = cap * 2;
            vm_scan_meta *grown = realloc(items, new_cap * sizeof(vm_scan_meta));
            if (grown == NULL)
            {
                free(items);
                return -1;
            }
            items = grown;
            cap = new_cap;
        }
        uint8_t has_data = (info.pages_resident != 0 || info.pages_swapped_out != 0 ||
                            info.pages_shared_now_private != 0 || info.pages_dirtied != 0)
                               ? 1
                               : 0;
        items[count++] = (vm_scan_meta){start, end, info.user_tag, has_data};
        addr = (mach_vm_address_t)end;
        depth = 1;
    }
    *out_items = items;
    *out_count = count;
    return 0;
}

static void record_scan_error(scan_ctx *ctx, int code, const char *fmt, uint64_t addr, size_t size)
{
    pthread_mutex_lock(&ctx->err_lock);
    if (!__atomic_load_n(&ctx->failed, __ATOMIC_RELAXED))
    {
        ctx->err_code = code;
        if (fmt != NULL)
            snprintf(ctx->err_msg, sizeof ctx->err_msg, fmt, (unsigned long long)addr, size);
        else
            snprintf(ctx->err_msg, sizeof ctx->err_msg, "out of memory");
        __atomic_store_n(&ctx->failed, 1, __ATOMIC_RELEASE);
    }
    pthread_mutex_unlock(&ctx->err_lock);
}

static void *scan_worker(void *arg)
{
    scan_ctx *ctx = (scan_ctx *)arg;
    uint8_t *scratch = malloc(SCAN_CHUNK_BYTES);
    if (scratch == NULL)
    {
        record_scan_error(ctx, 4, NULL, 0, 0);
        return NULL;
    }

    for (;;)
    {
        if (__atomic_load_n(&ctx->failed, __ATOMIC_RELAXED))
            break;
        size_t idx = __atomic_fetch_add(&ctx->next_item, 1, __ATOMIC_RELAXED);
        if (idx >= ctx->item_count)
            break;

        scan_work_item item = ctx->items[idx];
        if (ctx->skip_unreadable && __atomic_load_n(&ctx->region_skip[item.region_idx], __ATOMIC_RELAXED))
            continue;

        match_list *list = &ctx->results[idx];
        int skip_region = 0;
        for (uint64_t offset = 0; offset + 8 <= item.length && !skip_region; offset += SCAN_CHUNK_BYTES)
        {
            if (__atomic_load_n(&ctx->failed, __ATOMIC_RELAXED))
                break;
            if (ctx->skip_unreadable && __atomic_load_n(&ctx->region_skip[item.region_idx], __ATOMIC_RELAXED))
                break;

            uint64_t rem = item.length - offset;
            size_t length = rem < SCAN_CHUNK_BYTES ? (size_t)rem : SCAN_CHUNK_BYTES;
            mach_vm_size_t got = 0;
            kern_return_t kr = mach_vm_read_overwrite(ctx->task, (mach_vm_address_t)(item.base + offset),
                                                      (mach_vm_size_t)length, (mach_vm_address_t)scratch, &got);
            if (kr == KERN_SUCCESS && got == (mach_vm_size_t)length)
            {
                if (scan_words(list, item.base + offset, scratch, length,
                               ctx->vtables, ctx->vtable_count, ctx->min_vt, ctx->vt_span) != 0)
                {
                    record_scan_error(ctx, 4, NULL, 0, 0);
                    skip_region = 1;
                    break;
                }
                continue;
            }
            for (size_t page = 0; page < length; page += ctx->page_sz)
            {
                size_t size = length - page < ctx->page_sz ? length - page : ctx->page_sz;
                if (size < 8)
                    continue;
                mach_vm_size_t page_got = 0;
                kern_return_t page_kr = mach_vm_read_overwrite(ctx->task,
                                                               (mach_vm_address_t)(item.base + offset + page),
                                                               (mach_vm_size_t)size,
                                                               (mach_vm_address_t)(scratch + page), &page_got);
                if (page_kr != KERN_SUCCESS || page_got != (mach_vm_size_t)size)
                {
                    if (ctx->tolerate)
                        continue;
                    if (ctx->skip_unreadable)
                    {
                        if (item.is_region_start && offset == 0 && page == 0)
                        {
                            __atomic_store_n(&ctx->region_skip[item.region_idx], 1, __ATOMIC_RELAXED);
                            skip_region = 1;
                            break;
                        }
                        continue;
                    }
                    record_scan_error(ctx, 7,
                                      "Heap index is incomplete: writable range 0x%llx (%zu bytes) could not be read",
                                      item.base + offset + page, size);
                    skip_region = 1;
                    break;
                }
                if (scan_words(list, item.base + offset + page, scratch + page, size,
                               ctx->vtables, ctx->vtable_count, ctx->min_vt, ctx->vt_span) != 0)
                {
                    record_scan_error(ctx, 4, NULL, 0, 0);
                    skip_region = 1;
                    break;
                }
            }
        }
    }

    free(scratch);
    return NULL;
}

static void op_scan_vtables(int fd, reader *r)
{
    int32_t handle;
    uint8_t flags;
    uint32_t vtable_count;
    uint64_t vtables[MAX_SCAN_VTABLES];
    if (!rd_i32(r, &handle) || !rd_u8(r, &flags) || !rd_u32(r, &vtable_count) ||
        vtable_count == 0 || vtable_count > MAX_SCAN_VTABLES)
    {
        send_error(fd, 1, "malformed ScanVtables request");
        return;
    }
    task_slot *slot = slot_for(handle);
    if (slot == NULL)
    {
        send_error(fd, 5, "invalid task handle");
        return;
    }
    uint64_t min_vt = UINT64_MAX, max_vt = 0;
    for (uint32_t i = 0; i < vtable_count; i++)
    {
        if (!rd_u64(r, &vtables[i]))
        {
            send_error(fd, 1, "malformed ScanVtables vtable list");
            return;
        }
        if (vtables[i] < min_vt)
            min_vt = vtables[i];
        if (vtables[i] > max_vt)
            max_vt = vtables[i];
    }
    uint32_t region_count;
    if (!rd_u32(r, &region_count) || region_count > MAX_SCAN_REGIONS ||
        (size_t)(r->end - r->p) != (size_t)region_count * 16)
    {
        send_error(fd, 1, "malformed ScanVtables region list");
        return;
    }

    if (region_count == 0)
    {
        uint32_t empty[2] = {0, 0};
        send_frame(fd, empty, sizeof(empty));
        return;
    }

    uint64_t *reg_starts = malloc((size_t)region_count * sizeof(uint64_t));
    uint64_t *reg_sizes = malloc((size_t)region_count * sizeof(uint64_t));
    if (reg_starts == NULL || reg_sizes == NULL)
    {
        free(reg_starts);
        free(reg_sizes);
        send_error(fd, 4, "out of memory");
        return;
    }

    uint64_t min_addr = UINT64_MAX, max_addr = 0;
    for (uint32_t ri = 0; ri < region_count; ri++)
    {
        rd_u64(r, &reg_starts[ri]);
        rd_u64(r, &reg_sizes[ri]);
        if (reg_sizes[ri] >= 8)
        {
            if (reg_starts[ri] < min_addr)
                min_addr = reg_starts[ri];
            uint64_t reg_end = reg_starts[ri] + reg_sizes[ri];
            if (reg_end > max_addr)
                max_addr = reg_end;
        }
    }

    int tolerate = (flags & 1) != 0;
    int skip_unreadable = (flags & 2) != 0;

    vm_scan_meta *meta = NULL;
    size_t meta_count = 0;
    if (skip_unreadable && min_addr < max_addr)
    {
        if (collect_vm_scan_meta(slot->task, min_addr, max_addr, &meta, &meta_count) != 0)
        {
            meta = NULL;
            meta_count = 0;
        }
    }

    size_t work_cap = (size_t)region_count + 64;
    size_t work_count = 0;
    scan_work_item *items = malloc(work_cap * sizeof(scan_work_item));
    uint8_t *region_skip = calloc(region_count, 1);
    if (items == NULL || region_skip == NULL)
    {
        free(meta);
        free(reg_starts);
        free(reg_sizes);
        free(items);
        free(region_skip);
        send_error(fd, 4, "out of memory");
        return;
    }

    size_t mi = 0;
    uint64_t prev_start = 0;
    for (uint32_t ri = 0; ri < region_count; ri++)
    {
        uint64_t rs = reg_starts[ri];
        uint64_t rsz = reg_sizes[ri];
        if (rsz < 8)
            continue;
        if (meta_count > 0)
        {
            if (ri > 0 && rs < prev_start)
                mi = 0;
            prev_start = rs;
            while (mi < meta_count && meta[mi].end <= rs)
                mi++;
            uint64_t re = rs + rsz;
            if (mi < meta_count && meta[mi].start <= rs && meta[mi].end >= re &&
                (!meta[mi].has_data || is_non_heap_tag(meta[mi].user_tag)))
                continue;
        }
        for (uint64_t off = 0; off + 8 <= rsz; off += SCAN_WORK_BYTES)
        {
            uint64_t rem = rsz - off;
            uint64_t len = rem < SCAN_WORK_BYTES ? rem : SCAN_WORK_BYTES;
            if (work_count == work_cap)
            {
                size_t new_cap = work_cap * 2;
                scan_work_item *grown = realloc(items, new_cap * sizeof(scan_work_item));
                if (grown == NULL)
                {
                    free(meta);
                    free(reg_starts);
                    free(reg_sizes);
                    free(items);
                    free(region_skip);
                    send_error(fd, 4, "out of memory");
                    return;
                }
                items = grown;
                work_cap = new_cap;
            }
            items[work_count++] = (scan_work_item){ri, rs + off, len, off == 0 ? 1 : 0};
        }
    }

    free(meta);
    free(reg_starts);
    free(reg_sizes);

    if (work_count == 0)
    {
        free(items);
        free(region_skip);
        uint32_t empty[2] = {0, 0};
        send_frame(fd, empty, sizeof(empty));
        return;
    }

    match_list *results = calloc(work_count, sizeof(match_list));
    if (results == NULL)
    {
        free(items);
        free(region_skip);
        send_error(fd, 4, "out of memory");
        return;
    }

    size_t page_sz = (size_t)vm_page_size;
    if (page_sz < 4096)
        page_sz = 4096;

    scan_ctx ctx = {
        .task = slot->task,
        .vtables = vtables,
        .vtable_count = vtable_count,
        .min_vt = min_vt,
        .vt_span = max_vt - min_vt,
        .tolerate = tolerate,
        .skip_unreadable = skip_unreadable,
        .page_sz = page_sz,
        .items = items,
        .item_count = work_count,
        .results = results,
        .region_skip = region_skip,
        .next_item = 0,
        .failed = 0,
        .err_code = 4,
    };
    pthread_mutex_init(&ctx.err_lock, NULL);

    long ncpu = sysconf(_SC_NPROCESSORS_ONLN);
    size_t nthreads = ncpu > 1 ? (size_t)ncpu : 1;
    if (nthreads > SCAN_MAX_THREADS)
        nthreads = SCAN_MAX_THREADS;
    if (nthreads > work_count)
        nthreads = work_count;

    pthread_t threads[SCAN_MAX_THREADS];
    size_t spawned = 0;
    for (size_t t = 1; t < nthreads; t++)
    {
        if (pthread_create(&threads[spawned], NULL, scan_worker, &ctx) == 0)
            spawned++;
    }
    scan_worker(&ctx);
    for (size_t t = 0; t < spawned; t++)
        pthread_join(threads[t], NULL);

    pthread_mutex_destroy(&ctx.err_lock);

    if (ctx.failed)
    {
        int code = ctx.err_code;
        char msg[256];
        memcpy(msg, ctx.err_msg, sizeof(msg));
        for (size_t wi = 0; wi < work_count; wi++)
            free(results[wi].items);
        free(results);
        free(items);
        free(region_skip);
        send_error(fd, code, msg);
        return;
    }

    size_t total_matches = 0;
    for (size_t wi = 0; wi < work_count; wi++)
        total_matches += results[wi].count;

    if (total_matches > (size_t)(MAX_MESSAGE - 8u) / 16u)
    {
        for (size_t wi = 0; wi < work_count; wi++)
            free(results[wi].items);
        free(results);
        free(items);
        free(region_skip);
        send_error(fd, 4, "out of memory");
        return;
    }

    size_t frame_bytes = 8u + total_matches * 16u;
    uint8_t *frame_out = malloc(frame_bytes);
    if (frame_out == NULL)
    {
        for (size_t wi = 0; wi < work_count; wi++)
            free(results[wi].items);
        free(results);
        free(items);
        free(region_skip);
        send_error(fd, 4, "out of memory");
        return;
    }

    uint32_t ok_status = 0;
    uint32_t match_u32 = (uint32_t)total_matches;
    memcpy(frame_out, &ok_status, 4);
    memcpy(frame_out + 4, &match_u32, 4);
    uint8_t *dst = frame_out + 8;
    for (size_t wi = 0; wi < work_count; wi++)
    {
        if (results[wi].count > 0)
        {
            for (size_t k = 0; k < results[wi].count; k++)
            {
                memcpy(dst, &results[wi].items[k].addr, 8);
                memcpy(dst + 8, &results[wi].items[k].vt, 8);
                dst += 16;
            }
            free(results[wi].items);
        }
    }

    free(results);
    free(items);
    free(region_skip);
    send_frame(fd, frame_out, (uint32_t)frame_bytes);
    free(frame_out);
}

/* Read-only batching of small fields; the complete request is checked before the first Mach read. */
static void op_read_many(int fd, reader *r)
{
    int32_t handle;
    uint32_t count;
    uint64_t addresses[MAX_SCATTER_READS];
    uint32_t lengths[MAX_SCATTER_READS];
    if (!rd_i32(r, &handle) || !rd_u32(r, &count) || count == 0 || count > MAX_SCATTER_READS ||
        (size_t)(r->end - r->p) != (size_t)count * 12)
    {
        send_error(fd, 1, "malformed ReadMany request");
        return;
    }
    task_slot *slot = slot_for(handle);
    if (slot == NULL)
    {
        send_error(fd, 5, "invalid task handle");
        return;
    }
    for (uint32_t i = 0; i < count; i++)
    {
        if (!rd_u64(r, &addresses[i]) || !rd_u32(r, &lengths[i]) ||
            lengths[i] == 0 || lengths[i] > MAX_SCATTER_BYTES)
        {
            send_error(fd, 1, "invalid ReadMany field length");
            return;
        }
    }

    buffer out = {0};
    if (buf_reserve(&out, 8 + (size_t)count * (8 + MAX_SCATTER_BYTES)) != 0)
    {
        send_error(fd, 4, "out of memory");
        return;
    }
    buf_u32(&out, 0);
    buf_u32(&out, count);
    for (uint32_t i = 0; i < count; i++)
    {
        uint8_t data[MAX_SCATTER_BYTES];
        mach_vm_size_t got = 0;
        kern_return_t kr = mach_vm_read_overwrite(slot->task, (mach_vm_address_t)addresses[i],
            (mach_vm_size_t)lengths[i], (mach_vm_address_t)data, &got);
        int ok = kr == KERN_SUCCESS && got == (mach_vm_size_t)lengths[i];
        buf_u32(&out, ok ? 0 : 1);
        buf_u32(&out, ok ? lengths[i] : 0);
        if (ok) buf_bytes(&out, data, lengths[i]);
    }
    send_frame(fd, out.data, (uint32_t)out.len);
    free(out.data);
}

static void op_write(int fd, reader *r)
{
    int32_t handle;
    uint64_t address;
    uint32_t length;
    if (!rd_i32(r, &handle) || !rd_u64(r, &address) || !rd_u32(r, &length))
    {
        send_error(fd, 1, "malformed Write request");
        return;
    }
    task_slot *slot = slot_for(handle);
    if (slot == NULL)
    {
        send_error(fd, 5, "invalid task handle");
        return;
    }
    if ((uint64_t)(r->end - r->p) < (uint64_t)length)
    {
        send_error(fd, 1, "Write data is shorter than its length");
        return;
    }
    if (length == 0)
    {
        buffer out = {0};
        buf_u32(&out, 0);
        send_frame(fd, out.data, (uint32_t)out.len);
        free(out.data);
        return;
    }

    kern_return_t kr = mach_vm_write(slot->task, (mach_vm_address_t)address,
                                     (vm_offset_t)(uintptr_t)r->p, (mach_msg_type_number_t)length);
    if (kr != KERN_SUCCESS)
    {
        send_errorf(fd, 7, "mach_vm_write(0x%llx, %u) failed with kern_return_t %d",
                    (unsigned long long)address, (unsigned)length, (int)kr);
        return;
    }
    buffer out = {0};
    buf_u32(&out, 0);
    send_frame(fd, out.data, (uint32_t)out.len);
    free(out.data);
}

static void op_allocate(int fd, reader *r)
{
    int32_t handle;
    uint32_t bytes;
    if (!rd_i32(r, &handle) || !rd_u32(r, &bytes))
    {
        send_error(fd, 1, "malformed Allocate request");
        return;
    }
    task_slot *slot = slot_for(handle);
    if (slot == NULL)
    {
        send_error(fd, 5, "invalid task handle");
        return;
    }
    mach_vm_address_t address = 0;
    kern_return_t kr = mach_vm_allocate(slot->task, &address, (mach_vm_size_t)bytes, VM_FLAGS_ANYWHERE);
    if (kr != KERN_SUCCESS)
    {
        send_errorf(fd, 7, "mach_vm_allocate(%u) failed with kern_return_t %d", (unsigned)bytes, (int)kr);
        return;
    }
    buffer out = {0};
    buf_u32(&out, 0);
    buf_u64(&out, (uint64_t)address);
    send_frame(fd, out.data, (uint32_t)out.len);
    free(out.data);
}

/* ------------------------------------------------------------ remote calloc */

/* Assembled with clang -arch arm64 from:
 *   entry (x0 = data): mov x19,x0; add x0,x19,#0x30; mov x1,#0; adr x2,worker; mov x3,x19; ldr x8,[x19,#8]; blr x8;
 *                      add x9,x19,#0x2C; mov w8,#1; stlr w8,[x9]; 1: wfe; b 1b
 *   worker (x0 = data): stp x29,x30,[sp,#-32]!; mov x29,sp; str x19,[sp,#16]; mov x19,x0; ldr x0,[x19,#0x10];
 *                      ldr x1,[x19,#0x18]; ldr x8,[x19]; blr x8; str x0,[x19,#0x20]; add x9,x19,#0x28; mov w8,#1;
 *                      stlr w8,[x9]; ldr x19,[sp,#16]; ldp x29,x30,[sp],#32; mov x0,#0; ret
 * Data: +0 calloc, +8 pthread_create_from_mach_thread, +0x10 nmemb, +0x18 size, +0x20 result,
 *       +0x28 worker done, +0x2C creator done, +0x30 pthread_t. */
static const uint32_t k_calloc_stub[] = {
    0xAA0003F3, 0x9100C260, 0xD2800001, 0x10000122, 0xAA1303E3, 0xF9400668, 0xD63F0100, 0x9100B269,
    0x52800028, 0x889FFD28, 0xD503205F, 0x17FFFFFF, 0xA9BE7BFD, 0x910003FD, 0xF9000BF3, 0xAA0003F3,
    0xF9400A60, 0xF9400E61, 0xF9400268, 0xD63F0100, 0xF9001260, 0x9100A269, 0x52800028, 0x889FFD28,
    0xF9400BF3, 0xA8C27BFD, 0xD2800000, 0xD65F03C0};

#define CALLOC_PAGE 0x4000ULL
#define CALLOC_STACK 0x40000ULL
#define CALLOC_TIMEOUT_MS 3000

static int same_code(task_t task, const void *local, char *error, size_t error_len, const char *name)
{
    uint8_t remote[32];
    mach_vm_size_t got = 0;
    kern_return_t kr = mach_vm_read_overwrite(task, (mach_vm_address_t)(uintptr_t)local, sizeof remote,
                                              (mach_vm_address_t)(uintptr_t)remote, &got);
    if (kr != KERN_SUCCESS || got != sizeof remote || memcmp(remote, local, sizeof remote) != 0)
    {
        snprintf(error, error_len, "%s is not at the same address in the target (kern_return_t %d)", name, (int)kr);
        return 0;
    }
    return 1;
}

static int ensure_calloc_cave(task_slot *s, char *error, size_t error_len)
{
    if (s->calloc_code != 0)
        return 1;
    void *calloc_fn = dlsym(RTLD_DEFAULT, "calloc");
    void *create_fn = dlsym(RTLD_DEFAULT, "pthread_create_from_mach_thread");
    if (calloc_fn == NULL || create_fn == NULL)
    {
        snprintf(error, error_len, "calloc or pthread_create_from_mach_thread not found");
        return 0;
    }
    if (!same_code(s->task, calloc_fn, error, error_len, "calloc") ||
        !same_code(s->task, create_fn, error, error_len, "pthread_create_from_mach_thread"))
        return 0;

    mach_vm_address_t data = 0, code = 0, stack = 0;
    kern_return_t kr = mach_vm_allocate(s->task, &data, CALLOC_PAGE, VM_FLAGS_ANYWHERE);
    if (kr == KERN_SUCCESS) kr = mach_vm_allocate(s->task, &code, CALLOC_PAGE, VM_FLAGS_ANYWHERE);
    if (kr == KERN_SUCCESS) kr = mach_vm_allocate(s->task, &stack, CALLOC_STACK, VM_FLAGS_ANYWHERE);
    if (kr == KERN_SUCCESS)
        kr = mach_vm_write(s->task, code, (vm_offset_t)k_calloc_stub, (mach_msg_type_number_t)sizeof k_calloc_stub);
    if (kr == KERN_SUCCESS)
        kr = mach_vm_protect(s->task, code, CALLOC_PAGE, FALSE, VM_PROT_READ | VM_PROT_EXECUTE);
    uint64_t pointers[2] = {(uint64_t)(uintptr_t)calloc_fn, (uint64_t)(uintptr_t)create_fn};
    if (kr == KERN_SUCCESS)
        kr = mach_vm_write(s->task, data, (vm_offset_t)pointers, (mach_msg_type_number_t)sizeof pointers);
    if (kr != KERN_SUCCESS)
    {
        if (data) mach_vm_deallocate(s->task, data, CALLOC_PAGE);
        if (code) mach_vm_deallocate(s->task, code, CALLOC_PAGE);
        if (stack) mach_vm_deallocate(s->task, stack, CALLOC_STACK);
        snprintf(error, error_len, "remote calloc setup failed with kern_return_t %d", (int)kr);
        return 0;
    }
    s->calloc_data = data;
    s->calloc_code = code;
    s->calloc_stack_top = (stack + CALLOC_STACK - 0x100) & ~0xFULL;
    return 1;
}

static int remote_call2(task_slot *s, void *fn, const char *fn_name, uint64_t arg0, uint64_t arg1,
                        uint64_t *result, char *error, size_t error_len)
{
    if (s->paused)
    {
        snprintf(error, error_len, "the game is paused; %s could wait on a suspended thread's lock", fn_name);
        return 0;
    }
    if (!ensure_calloc_cave(s, error, error_len))
        return 0;
    if (!same_code(s->task, fn, error, error_len, fn_name))
        return 0;
    uint64_t fn_ptr = (uint64_t)(uintptr_t)fn;
    kern_return_t kr = mach_vm_write(s->task, s->calloc_data, (vm_offset_t)&fn_ptr, (mach_msg_type_number_t)sizeof fn_ptr);
    if (kr != KERN_SUCCESS)
    {
        snprintf(error, error_len, "remote %s fn pointer: kern_return_t %d", fn_name, (int)kr);
        return 0;
    }
    uint64_t args[6] = {arg0, arg1, 0, 0, 0, 0}; /* arg0, arg1, result, flags, pthread_t */
    kr = mach_vm_write(s->task, s->calloc_data + 0x10, (vm_offset_t)args, (mach_msg_type_number_t)sizeof args);
    if (kr != KERN_SUCCESS)
    {
        snprintf(error, error_len, "remote %s arguments: kern_return_t %d", fn_name, (int)kr);
        return 0;
    }
    arm_thread_state64_t state;
    memset(&state, 0, sizeof state);
    state.__x[0] = s->calloc_data;
    state.__sp = s->calloc_stack_top;
    state.__pc = s->calloc_code;
    thread_act_t thread = MACH_PORT_NULL;
    kr = thread_create_running(s->task, ARM_THREAD_STATE64, (thread_state_t)&state, ARM_THREAD_STATE64_COUNT, &thread);
    if (kr != KERN_SUCCESS)
    {
        snprintf(error, error_len, "thread_create_running failed with kern_return_t %d", (int)kr);
        return 0;
    }
    uint64_t block[3] = {0};
    int done = 0;
    for (int waited = 0; waited < CALLOC_TIMEOUT_MS; waited++)
    {
        mach_vm_size_t got = 0;
        if (mach_vm_read_overwrite(s->task, s->calloc_data + 0x20, sizeof block, (mach_vm_address_t)(uintptr_t)block, &got) == KERN_SUCCESS &&
            got == sizeof block && (uint32_t)block[1] == 1 && (uint32_t)(block[1] >> 32) == 1)
        {
            done = 1;
            break;
        }
        usleep(1000);
    }
    thread_terminate(thread);
    mach_port_deallocate(mach_task_self(), thread);
    if (!done)
    {
        s->calloc_code = 0; /* a late worker could still store into this data block; never reuse it */
        snprintf(error, error_len, "remote %s timed out", fn_name);
        return 0;
    }
    *result = block[0];
    return 1;
}

__attribute__((unused)) /* the plugin calls calloc directly */
static int remote_calloc(task_slot *s, uint32_t bytes, uint64_t *result, char *error, size_t error_len)
{
    void *calloc_fn = dlsym(RTLD_DEFAULT, "calloc");
    if (calloc_fn == NULL)
    {
        snprintf(error, error_len, "calloc not found");
        return 0;
    }
    if (!remote_call2(s, calloc_fn, "calloc", 1, bytes, result, error, error_len))
        return 0;
    if (*result == 0)
    {
        snprintf(error, error_len, "remote calloc(%u) returned NULL", (unsigned)bytes);
        return 0;
    }
    return 1;
}

static void op_heap_check(int fd, reader *r)
{
    int32_t handle;
    uint64_t addr;
    if (!rd_i32(r, &handle) || !rd_u64(r, &addr) || addr == 0)
    {
        send_error(fd, 1, "malformed HeapCheck request");
        return;
    }
    task_slot *slot = slot_for(handle);
    if (slot == NULL)
    {
        send_error(fd, 5, "invalid task handle");
        return;
    }
    uint32_t user_tag = 0;
    uint64_t reg_start = 0, reg_size = 0, malloc_sz = 0;
    if (!query_address_tag(slot->task, addr, &user_tag, &reg_start, &reg_size))
    {
        send_errorf(fd, 7, "no VM region mapped for 0x%llx", (unsigned long long)addr);
        return;
    }
    if (!slot->paused)
    {
        void *ms_fn = dlsym(RTLD_DEFAULT, "malloc_size");
        char error[256];
        if (ms_fn != NULL)
            (void)remote_call2(slot, ms_fn, "malloc_size", addr, 0, &malloc_sz, error, sizeof error);
    }
    buffer out = {0};
    buf_u32(&out, 0);
    buf_u32(&out, user_tag);
    buf_u64(&out, malloc_sz);
    buf_u64(&out, reg_start);
    buf_u64(&out, reg_size);
    send_frame(fd, out.data, (uint32_t)out.len);
    free(out.data);
}

static void op_remote_calloc(int fd, reader *r)
{
    int32_t handle;
    uint32_t bytes;
    if (!rd_i32(r, &handle) || !rd_u32(r, &bytes) || bytes == 0 || bytes > 16u * 1024u * 1024u)
    {
        send_error(fd, 1, "malformed RemoteCalloc request");
        return;
    }
    task_slot *slot = slot_for(handle);
    if (slot == NULL)
    {
        send_error(fd, 5, "invalid task handle");
        return;
    }
    uint64_t address = 0;
#ifdef FM27_PLUGIN
    /* Inside the game: the game's own calloc, so the game can free() the block later. */
    void *block = calloc(1, bytes);
    if (block == NULL)
    {
        send_error(fd, 7, "calloc returned NULL");
        return;
    }
    address = (uint64_t)(uintptr_t)block;
#else
    char error[256];
    if (!remote_calloc(slot, bytes, &address, error, sizeof error))
    {
        send_error(fd, 7, error);
        return;
    }
#endif
    buffer out = {0};
    buf_u32(&out, 0);
    buf_u64(&out, address);
    send_frame(fd, out.data, (uint32_t)out.len);
    free(out.data);
}

static void op_allocate_near(int fd, reader *r)
{
    int32_t handle;
    uint64_t near;
    uint32_t bytes;
    if (!rd_i32(r, &handle) || !rd_u64(r, &near) || !rd_u32(r, &bytes))
    {
        send_error(fd, 1, "malformed AllocateNear request");
        return;
    }
    task_slot *slot = slot_for(handle);
    if (slot == NULL)
    {
        send_error(fd, 5, "invalid task handle");
        return;
    }

    uint64_t address = 0;
    if (!allocate_near(slot->task, near, bytes, &address))
    {
        send_errorf(fd, 8, "no free memory within %llu MB of 0x%llx",
                    (unsigned long long)(BRANCH_REACH >> 20), (unsigned long long)near);
        return;
    }
    buffer out = {0};
    buf_u32(&out, 0);
    buf_u64(&out, address);
    send_frame(fd, out.data, (uint32_t)out.len);
    free(out.data);
}

static void op_protect(int fd, reader *r)
{
    int32_t handle;
    uint64_t address;
    uint32_t bytes;
    uint32_t protection;
    uint8_t copy;
    if (!rd_i32(r, &handle) || !rd_u64(r, &address) || !rd_u32(r, &bytes) ||
        !rd_u32(r, &protection) || !rd_u8(r, &copy))
    {
        send_error(fd, 1, "malformed Protect request");
        return;
    }
    task_slot *slot = slot_for(handle);
    if (slot == NULL)
    {
        send_error(fd, 5, "invalid task handle");
        return;
    }

    int previous = 0;
    uint32_t previous_page = PAGE_EXECUTE_READ;
    if (current_protection(slot->task, address, &previous))
        previous_page = from_vm_protection(previous);

    kern_return_t kr = mach_vm_protect(slot->task, (mach_vm_address_t)address, (mach_vm_size_t)bytes,
                                       copy ? TRUE : FALSE, (vm_prot_t)protection);
    if (kr != KERN_SUCCESS)
    {
        send_errorf(fd, 9, "mach_vm_protect(0x%llx, %u, 0x%x) failed with kern_return_t %d",
                    (unsigned long long)address, (unsigned)bytes, (unsigned)protection, (int)kr);
        return;
    }
    buffer out = {0};
    buf_u32(&out, 0);
    buf_u32(&out, previous_page);
    send_frame(fd, out.data, (uint32_t)out.len);
    free(out.data);
}

static void op_free(int fd, reader *r)
{
    int32_t handle;
    uint64_t address;
    uint64_t bytes;
    if (!rd_i32(r, &handle) || !rd_u64(r, &address) || !rd_u64(r, &bytes))
    {
        send_error(fd, 1, "malformed Free request");
        return;
    }
    task_slot *slot = slot_for(handle);
    if (slot == NULL)
    {
        send_error(fd, 5, "invalid task handle");
        return;
    }
    uint64_t size = (bytes + ALLOC_GRANULARITY - 1) & ~(ALLOC_GRANULARITY - 1);
    kern_return_t kr = mach_vm_deallocate(slot->task, (mach_vm_address_t)address, (mach_vm_size_t)size);
    if (kr != KERN_SUCCESS)
    {
        send_errorf(fd, 10, "mach_vm_deallocate(0x%llx, %llu) failed with kern_return_t %d",
                    (unsigned long long)address, (unsigned long long)size, (int)kr);
        return;
    }
    buffer out = {0};
    buf_u32(&out, 0);
    send_frame(fd, out.data, (uint32_t)out.len);
    free(out.data);
}

static void op_enumerate(int fd, reader *r)
{
    int32_t handle;
    uint64_t image_start;
    uint64_t image_end;
    if (!rd_i32(r, &handle) || !rd_u64(r, &image_start) || !rd_u64(r, &image_end))
    {
        send_error(fd, 1, "malformed Enumerate request");
        return;
    }
    task_slot *slot = slot_for(handle);
    if (slot == NULL)
    {
        send_error(fd, 5, "invalid task handle");
        return;
    }

    buffer records = {0};
    if (collect_writable(slot->task, image_start, image_end, &records) != 0)
    {
        free(records.data);
        send_error(fd, 4, "out of memory");
        return;
    }

    uint32_t count = (uint32_t)(records.len / 17);
    buffer out = {0};
    if (buf_u32(&out, 0) != 0 || buf_u32(&out, count) != 0 || buf_bytes(&out, records.data, records.len) != 0)
    {
        free(records.data);
        free(out.data);
        send_error(fd, 4, "out of memory");
        return;
    }
    free(records.data);
    send_frame(fd, out.data, (uint32_t)out.len);
    free(out.data);
}

static void op_close(int fd, reader *r)
{
    int32_t handle;
    if (!rd_i32(r, &handle))
    {
        send_error(fd, 1, "malformed Close request");
        return;
    }
    task_slot *slot = slot_for(handle);
    if (slot != NULL)
        close_slot(slot);
    buffer out = {0};
    buf_u32(&out, 0);
    send_frame(fd, out.data, (uint32_t)out.len);
    free(out.data);
}

static void op_dyld_info(int fd, reader *r)
{
    int32_t handle;
    if (!rd_i32(r, &handle))
    {
        send_error(fd, 1, "malformed DyldInfo request");
        return;
    }
    task_slot *slot = slot_for(handle);
    if (slot == NULL)
    {
        send_error(fd, 5, "invalid task handle");
        return;
    }

    task_dyld_info_data_t info;
    mach_msg_type_number_t count = TASK_DYLD_INFO_COUNT;
    memset(&info, 0, sizeof(info));
    kern_return_t kr = task_info(slot->task, TASK_DYLD_INFO, (task_info_t)&info, &count);
    if (kr != KERN_SUCCESS)
    {
        send_errorf(fd, 11, "task_info(TASK_DYLD_INFO) failed with kern_return_t %d", (int)kr);
        return;
    }
    buffer out = {0};
    buf_u32(&out, 0);
    buf_u64(&out, (uint64_t)info.all_image_info_addr);
    buf_u64(&out, (uint64_t)info.all_image_info_size);
    send_frame(fd, out.data, (uint32_t)out.len);
    free(out.data);
}

static void op_pause(int fd, reader *r)
{
    int32_t handle;
    if (!rd_i32(r, &handle))
    {
        send_error(fd, 1, "malformed Pause request");
        return;
    }
    task_slot *slot = slot_for(handle);
    if (slot == NULL)
    {
        send_error(fd, 5, "invalid task handle");
        return;
    }
    if (slot->paused)
    {
        send_error(fd, 12, "task is already paused");
        return;
    }

    thread_act_array_t threads = NULL;
    mach_msg_type_number_t count = 0;
    kern_return_t kr = task_threads(slot->task, &threads, &count);
    if (kr != KERN_SUCCESS || threads == NULL || count == 0)
    {
        if (threads != NULL)
            mach_vm_deallocate(mach_task_self(), (mach_vm_address_t)threads,
                               (mach_vm_size_t)count * sizeof(thread_act_t));
        send_errorf(fd, 13, "task_threads failed with kern_return_t %d", (int)kr);
        return;
    }
#ifdef FM27_PLUGIN
    /* The serving thread must keep running to answer Resume. */
    thread_act_t self = mach_thread_self();
    for (mach_msg_type_number_t i = 0; i < count; i++)
        if (threads[i] == self)
        {
            mach_port_deallocate(mach_task_self(), threads[i]);
            threads[i] = threads[count - 1];
            count--;
            break;
        }
    mach_port_deallocate(mach_task_self(), self);
#endif

    mach_msg_type_number_t suspended = 0;
    for (; suspended < count; suspended++)
    {
        kr = thread_suspend(threads[suspended]);
        if (kr == KERN_SUCCESS)
            continue;
        for (mach_msg_type_number_t i = 0; i < suspended; i++)
            thread_resume(threads[i]);
        for (mach_msg_type_number_t i = 0; i < count; i++)
            mach_port_deallocate(mach_task_self(), threads[i]);
        mach_vm_deallocate(mach_task_self(), (mach_vm_address_t)threads,
                           (mach_vm_size_t)count * sizeof(thread_act_t));
        send_errorf(fd, 13, "thread_suspend failed with kern_return_t %d", (int)kr);
        return;
    }

    slot->paused = 1;
    slot->threads = threads;
    slot->thread_count = count;

    buffer out = {0};
    buf_u32(&out, 0);
    buf_u32(&out, 0);
    send_frame(fd, out.data, (uint32_t)out.len);
    free(out.data);
}

static void op_resume(int fd, reader *r)
{
    int32_t handle;
    if (!rd_i32(r, &handle))
    {
        send_error(fd, 1, "malformed Resume request");
        return;
    }
    task_slot *slot = slot_for(handle);
    if (slot == NULL)
    {
        send_error(fd, 5, "invalid task handle");
        return;
    }
    resume_task(slot);
    buffer out = {0};
    buf_u32(&out, 0);
    send_frame(fd, out.data, (uint32_t)out.len);
    free(out.data);
}

static void handle_request(int fd, int client_id, const uint8_t *request, uint32_t length)
{
    if (length < 1)
    {
        send_error(fd, 1, "empty request");
        return;
    }
    uint8_t opcode = request[0];
    reader r = {request + 1, request + length};
    switch (opcode)
    {
    case OP_OPEN:
        op_open(fd, client_id, &r);
        break;
    case OP_READ:
        op_read(fd, &r);
        break;
    case OP_READ_MANY:
        op_read_many(fd, &r);
        break;
    case OP_REMOTE_CALLOC:
        op_remote_calloc(fd, &r);
        break;
    case OP_HEAP_CHECK:
        op_heap_check(fd, &r);
        break;
    case OP_SCAN_VTABLES:
        op_scan_vtables(fd, &r);
        break;
    case OP_WRITE:
        op_write(fd, &r);
        break;
    case OP_ALLOCATE:
        op_allocate(fd, &r);
        break;
    case OP_ALLOCATE_NEAR:
        op_allocate_near(fd, &r);
        break;
    case OP_PROTECT:
        op_protect(fd, &r);
        break;
    case OP_FREE:
        op_free(fd, &r);
        break;
    case OP_ENUMERATE:
        op_enumerate(fd, &r);
        break;
    case OP_CLOSE:
        op_close(fd, &r);
        break;
    case OP_DYLD_INFO:
        op_dyld_info(fd, &r);
        break;
    case OP_PAUSE:
        op_pause(fd, &r);
        break;
    case OP_RESUME:
        op_resume(fd, &r);
        break;
    default:
        send_errorf(fd, 14, "unknown opcode %u", (unsigned)opcode);
        break;
    }
}

static void serve_client(int fd, int client_id)
{
    for (;;)
    {
        uint32_t length = 0;
        if (read_exact(fd, &length, 4) != 0)
            break;
        if (length == 0 || length > MAX_MESSAGE)
            break;
        uint8_t *request = malloc(length);
        if (request == NULL)
            break;
        if (read_exact(fd, request, length) != 0)
        {
            free(request);
            break;
        }
        handle_request(fd, client_id, request, length);
        free(request);
    }
    close_client_tasks(client_id);
}

/* --------------------------------------------------------------- main loop */

#ifdef FM27_PLUGIN
#include <libgen.h>
#include <limits.h>
#include <mach-o/dyld.h>

static int g_plugin_listener = -1;

/* Steam passes the launch options to helpers it starts with the game: serve only in the game itself. */
static int plugin_in_game(void)
{
    char path[PATH_MAX];
    uint32_t size = sizeof path;
    if (_NSGetExecutablePath(path, &size) != 0)
        return 0;
    /* Any Football Manager release, so one setup keeps working when the next game comes out. */
    const char *name = basename(path);
    return strncmp(name, "Football Manager", 16) == 0 || strcmp(name, "fm") == 0;
}

static void *plugin_serve(void *unused)
{
    (void)unused;
    int next_client_id = 1;
    for (;;)
    {
        int client = accept(g_plugin_listener, NULL, NULL);
        if (client < 0)
        {
            if (errno == EINTR || errno == ECONNABORTED)
                continue;
            return NULL;
        }
        /* One client at a time, as in the helper; the editor serialises its own requests. */
        int client_id = next_client_id++;
        serve_client(client, client_id);
        close(client);
    }
}

__attribute__((constructor)) static void plugin_start(void)
{
    /* Processes the game starts must not inherit the plugin: dyld aborts platform binaries such as /usr/bin/open
       when an unsigned library is inserted (measured: SIGABRT in dyld4::prepare). */
    unsetenv("DYLD_INSERT_LIBRARIES");
    if (!plugin_in_game())
        return;
    char directory[PATH_MAX];
    if (confstr(_CS_DARWIN_USER_TEMP_DIR, directory, sizeof directory) == 0)
        return;
    struct sockaddr_un address;
    memset(&address, 0, sizeof(address));
    address.sun_family = AF_UNIX;
    int written = snprintf(address.sun_path, sizeof address.sun_path, "%sfm27editor-%d.sock", directory, (int)getpid());
    if (written <= 0 || (size_t)written >= sizeof address.sun_path)
        return;

    int fd = socket(AF_UNIX, SOCK_STREAM, 0);
    if (fd < 0)
        return;
    unlink(address.sun_path);
    mode_t previous = umask(0077); /* owner-only from the moment the socket exists */
    int bound = bind(fd, (struct sockaddr *)&address, sizeof(address));
    umask(previous);
    if (bound != 0 || listen(fd, 4) != 0)
    {
        close(fd);
        return;
    }
    g_plugin_listener = fd;
    signal(SIGPIPE, SIG_IGN);
    pthread_t thread;
    if (pthread_create(&thread, NULL, plugin_serve, NULL) != 0)
    {
        close(fd);
        unlink(address.sun_path);
        g_plugin_listener = -1;
        return;
    }
    pthread_detach(thread);
}
#else

static int make_fallback_listener(void)
{
    /* Manual testing only: production starts through launch_activate_socket, so never take over the
       launchd socket path behind launchd's back. */
    const char *path = getenv("FM27_HELPER_SOCKET");
    if (path == NULL)
        return -1;

    int fd = socket(AF_UNIX, SOCK_STREAM, 0);
    if (fd < 0)
        return -1;

    struct sockaddr_un address;
    memset(&address, 0, sizeof(address));
    address.sun_family = AF_UNIX;
    strncpy(address.sun_path, path, sizeof(address.sun_path) - 1);
    unlink(path);
    if (bind(fd, (struct sockaddr *)&address, sizeof(address)) != 0)
    {
        close(fd);
        return -1;
    }
    if (chmod(path, 0666) != 0)
    {
        close(fd);
        return -1;
    }
    if (listen(fd, 8) != 0)
    {
        close(fd);
        return -1;
    }
    return fd;
}

int main(void)
{
    signal(SIGPIPE, SIG_IGN);

    int *launch_fds = NULL;
    size_t launch_count = 0;
    int listeners[8];
    int listener_count = 0;

    if (launch_activate_socket("Fm27Socket", &launch_fds, &launch_count) == 0 &&
        launch_fds != NULL && launch_count > 0)
    {
        for (size_t i = 0; i < launch_count && listener_count < 8; i++)
            listeners[listener_count++] = launch_fds[i];
    }
    else
    {
        int fd = make_fallback_listener();
        if (fd < 0)
        {
            fprintf(stderr, "fm27_helper: not managed by launchd (set FM27_HELPER_SOCKET for manual runs)\n");
            return 1;
        }
        listeners[listener_count++] = fd;
    }

    time_t last_activity = time(NULL);
    int next_client_id = 1;

    for (;;)
    {
        int client = -1;
        int client_id = 0;
        struct pollfd fds[8];
        int nfds = 0;
        for (int i = 0; i < listener_count; i++)
        {
            fds[nfds].fd = listeners[i];
            fds[nfds].events = POLLIN;
            fds[nfds].revents = 0;
            nfds++;
        }

        time_t now = time(NULL);
        if (now - last_activity >= IDLE_SECONDS)
            break;
        int timeout = (int)((IDLE_SECONDS - (now - last_activity)) * 1000);
        if (timeout < 0)
            timeout = 0;

        int ready = poll(fds, (nfds_t)nfds, timeout);
        if (ready < 0)
        {
            if (errno == EINTR)
                continue;
            break;
        }
        if (ready == 0)
        {
            if (time(NULL) - last_activity >= IDLE_SECONDS)
                break;
            continue;
        }

        for (int i = 0; i < nfds; i++)
        {
            if ((fds[i].revents & POLLIN) == 0)
                continue;
            int fd = accept(listeners[i], NULL, NULL);
            if (fd < 0)
                continue;
            client = fd;
            client_id = next_client_id++;
            break;
        }
        if (client < 0)
            continue;

        /* One client at a time; the editor serialises its own requests. */
        serve_client(client, client_id);
        close(client);
        last_activity = time(NULL);
    }

    for (int i = 0; i < listener_count; i++)
        close(listeners[i]);
    if (launch_fds != NULL)
        free(launch_fds);

    for (int i = 0; i < MAX_TASKS; i++)
        close_slot(&g_tasks[i]);
    return 0;
}
#endif
