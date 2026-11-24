using System.Collections.Concurrent;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Threading;
using Fm27Editor.App;

namespace Fm27Editor.App.Tests;

/// <summary>
/// Avalonia controls can only be created on the thread that set Avalonia up. Test methods run on whatever worker thread
/// xunit picks, so the setup used to land on a different thread than some tests ("Call from invalid thread", about one
/// run in eight). One dedicated thread sets Avalonia up on the first <see cref="Run"/> and executes all UI work; tests
/// send their UI work to it through <see cref="Run"/>.
/// </summary>
/// <remarks>
/// On macOS the desktop platform is not usable from a worker thread at all: AppKit insists on the process main thread
/// for <c>NSWindow</c> ("NSWindow should only be instantiated on the main thread!"), which is inside the test host's own
/// run loop. The tests therefore use Avalonia's headless platform off Windows — same controls, layout and Skia
/// rendering, no window server. Windows keeps the desktop platform, whose Win32 backend is thread agnostic.
/// <para>
/// Setup starts from <see cref="Run"/> rather than from a <c>[ModuleInitializer]</c>: the module initializer runs while
/// the assembly load lock is held, and a thread started there blocks in the JIT (resolving tokens calls
/// <c>AppDomain::LoadAssembly</c>) until the initializer returns, which it cannot do while it waits for the new thread.
/// </para>
/// <para>
/// The UI thread takes a test straight off a queue and runs it itself, then keeps draining dispatcher jobs while the
/// next test is awaited. The previous <c>Dispatcher.UIThread.Invoke</c> hop handed the test to a dispatcher job, so the
/// test, its <c>RunJobs</c> calls, the layout pass and the 60 FPS headless render timer all ended up nested inside one
/// another; that showed up as "Visual was invalidated during the render pass" and as a null key out of the layout queue.
/// Running the test as plain code and keeping the dispatcher pumping around it removes those layers.
/// </para>
/// <para>
/// <c>Dispatcher.UIThread.CheckAccess</c> cannot decide whether the caller may run the action itself: the headless
/// platform binds a dispatcher implementation whose <c>CurrentThreadIsLoopThread</c> is always true, so every caller
/// would take the shortcut. The action therefore runs inline only on <see cref="uiThread"/> itself; everything else
/// goes through the queue.
/// </para>
/// </remarks>
static class AvaloniaTestThread
{
    static readonly object StartGate = new();
    static readonly BlockingCollection<(Action Action, TaskCompletionSource Done)> Queue = new();
    static Thread? uiThread;
    static Exception? startFailure;

    static void EnsureStarted()
    {
        lock (StartGate)
        {
            if (uiThread is not null) return;
            using var ready = new ManualResetEventSlim();
            var thread = new Thread(() =>
            {
                // Create the dispatcher on this thread: the platform binds its loop thread when the dispatcher is built.
                _ = Dispatcher.UIThread.CheckAccess();
                try
                {
                    AppBuilder builder = Program.BuildAvaloniaApp();
                    if (!OperatingSystem.IsWindows()) builder = builder.UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
                    builder.SetupWithoutStarting();
                }
                catch (Exception ex) { startFailure = ex; }
                finally { ready.Set(); }
                if (startFailure is not null) return;
                while (true)
                {
                    if (Queue.TryTake(out var test, 1))
                    {
                        try { test.Action(); test.Done.SetResult(); }
                        catch (Exception ex) { test.Done.SetException(ex); }
                    }
                    // Timers, Post callbacks and whatever Show/Close queued keep flowing, also while a test is awaited.
                    try { Dispatcher.UIThread.RunJobs(); }
                    catch (InvalidOperationException) { }    // an Invoke from a test is already running jobs right now
                    catch (Exception ex)
                    {
                        // A render-timer job that throws would otherwise escape on a background thread and abort the run.
                        Console.WriteLine($"UI thread job failed: {ex}");
                    }
                }
            }) { IsBackground = true, Name = "Avalonia test UI thread" };
            thread.Start();
            ready.Wait();
            uiThread = thread;
        }
        if (startFailure is not null) throw new InvalidOperationException("Avalonia could not be set up for the tests", startFailure);
    }

    /// <summary>Runs queued dispatcher jobs. On the Windows desktop platform a compositor pass can briefly suspend
    /// dispatcher processing; wait for it to finish, and let a suspension that does not end fail the test.</summary>
    public static void RunJobs()
    {
        var waited = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            try
            {
                Dispatcher.UIThread.RunJobs();
                return;
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("processing is suspended", StringComparison.Ordinal) && waited.Elapsed < TimeSpan.FromSeconds(5))
            {
                Thread.Sleep(1);
            }
        }
    }

    /// <summary>Runs <paramref name="action"/> on the UI thread and rethrows what it throws.</summary>
    public static void Run(Action action)
    {
        EnsureStarted();
        // <c>Dispatcher.UIThread.CheckAccess</c> is not usable here: the headless platform binds a legacy dispatcher impl
        // whose <c>CurrentThreadIsLoopThread</c> is always true, so every caller would think it is the UI thread and two
        // threads end up driving the dispatcher and the layout queue at once.
        if (ReferenceEquals(Thread.CurrentThread, uiThread)) { action(); return; }
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Queue.Add((action, done));
        done.Task.GetAwaiter().GetResult();
    }
}
