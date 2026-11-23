using Fm27Editor.Core.Game;

namespace Fm27Editor.Core.Tests;

public class MacAccessMessageTests
{
    // The helper's text, as a user reported it on 22-11-2026.
    const string Refused = "Open(pid 75498) failed: task_for_pid(75498) failed with kern_return_t 5";

    [Fact]
    public void A_refused_task_for_pid_points_to_the_plugin_setup() =>
        Assert.Contains("Set up the editor plugin", MacProcessDiscovery.Explain(Refused));

    [Fact]
    public void Other_errors_are_left_as_they_are() =>
        Assert.All(new[] { "task_for_pid(1) failed with kern_return_t 15", "task_for_pid(1) failed with kern_return_t 50" },
            message => Assert.Equal(message, MacProcessDiscovery.Explain(message)));
}
