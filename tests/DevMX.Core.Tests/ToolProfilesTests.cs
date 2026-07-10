using System.Text.Json.Nodes;
using Xunit;

namespace DevMX.Core.Tests;

public class ToolProfilesTests
{
    private static ToolDefinition MakeTool(string name) =>
        new(name, "desc", new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() });

    [Fact]
    public void Filter_Full_ReturnsAllTools()
    {
        var tools = new[] { MakeTool("read_file"), MakeTool("patch_file"), MakeTool("run_shell") };
        var result = ToolProfiles.Filter(tools, ToolProfiles.Full);
        Assert.Equal(3, result.Count);
    }

    [Fact]
    public void Filter_Restricted_KeepsOnlyAllowlistedTools()
    {
        var tools = new[]
        {
            MakeTool("read_file"),
            MakeTool("grep_file"),
            MakeTool("patch_file"),
            MakeTool("create_file"),
            MakeTool("run_shell"),
            MakeTool("devmind_task_start"),
            MakeTool("devmind_task_status"),
            MakeTool("devmind_task_result"),
            MakeTool("devmind_task_cancel"),
            MakeTool("devmind_task_continue"),
            MakeTool("devmind_task_list"),
            MakeTool("find_in_files"),
            MakeTool("find_symbol"),
            MakeTool("list_files"),
            MakeTool("go_to_definition"),
            MakeTool("find_references"),
            MakeTool("hover"),
            MakeTool("get_diagnostics"),
            MakeTool("diff_file"),
            MakeTool("run_build"),
            MakeTool("run_tests"),
            MakeTool("web_search"),
            MakeTool("http_request"),
        };
        var result = ToolProfiles.Filter(tools, ToolProfiles.Restricted);
        var names = result.Select(t => t.Name).OrderBy(n => n).ToList();

        // All allowlisted tools present
        Assert.Contains("read_file", names);
        Assert.Contains("grep_file", names);
        Assert.Contains("devmind_task_start", names);
        Assert.Contains("devmind_task_status", names);
        Assert.Contains("devmind_task_result", names);
        Assert.Contains("devmind_task_cancel", names);
        Assert.Contains("devmind_task_continue", names);
        Assert.Contains("devmind_task_list", names);
        Assert.Contains("find_in_files", names);
        Assert.Contains("find_symbol", names);
        Assert.Contains("list_files", names);
        Assert.Contains("go_to_definition", names);
        Assert.Contains("find_references", names);
        Assert.Contains("hover", names);
        Assert.Contains("get_diagnostics", names);
        Assert.Contains("diff_file", names);
        Assert.Contains("run_build", names);
        Assert.Contains("run_tests", names);

        // Excluded tools NOT present
        Assert.DoesNotContain("patch_file", names);
        Assert.DoesNotContain("create_file", names);
        Assert.DoesNotContain("run_shell", names);
        Assert.DoesNotContain("web_search", names);
        Assert.DoesNotContain("http_request", names);

        // Count: 18 allowlisted tools
        Assert.Equal(18, result.Count);
    }

    [Fact]
    public void Filter_UnknownProfile_ReturnsAllTools()
    {
        var tools = new[] { MakeTool("read_file"), MakeTool("patch_file") };
        var result = ToolProfiles.Filter(tools, "some_unknown_profile");
        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void IsToolAllowed_Full_AllowsEverything()
    {
        Assert.True(ToolProfiles.IsToolAllowed("patch_file", ToolProfiles.Full));
        Assert.True(ToolProfiles.IsToolAllowed("read_file", ToolProfiles.Full));
        Assert.True(ToolProfiles.IsToolAllowed("run_shell", ToolProfiles.Full));
    }

    [Fact]
    public void IsToolAllowed_Restricted_DeniesExcludedTools()
    {
        Assert.True(ToolProfiles.IsToolAllowed("read_file", ToolProfiles.Restricted));
        Assert.False(ToolProfiles.IsToolAllowed("patch_file", ToolProfiles.Restricted));
        Assert.False(ToolProfiles.IsToolAllowed("create_file", ToolProfiles.Restricted));
        Assert.False(ToolProfiles.IsToolAllowed("run_shell", ToolProfiles.Restricted));
        Assert.True(ToolProfiles.IsToolAllowed("devmind_task_start", ToolProfiles.Restricted));
    }

    [Fact]
    public void DenyMessage_ContainsToolNameAndHint()
    {
        var msg = ToolProfiles.DenyMessage("patch_file");
        Assert.Contains("patch_file", msg);
        Assert.Contains("[denied]", msg);
        Assert.Contains("devmind_task_start", msg);
    }
}
