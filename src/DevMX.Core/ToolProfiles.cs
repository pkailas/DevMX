using System.Collections.Generic;

namespace DevMX.Core;

/// <summary>
/// Static utility for tool-profile filtering. Controls which tools the LLM sees
/// and can execute, based on the active profile (full vs restricted).
/// </summary>
public static class ToolProfiles
{
    public const string Full = "full";
    public const string Restricted = "restricted";
    public const string Auto = "auto";

    /// <summary>
    /// Tool names allowed under the restricted profile (read-only + delegation tools).
    /// </summary>
    private static readonly HashSet<string> RestrictedAllowlist = new()
    {
        "read_file", "grep_file", "find_in_files", "find_symbol", "list_files",
        "go_to_definition", "find_references", "hover", "get_diagnostics",
        "diff_file", "run_build", "run_tests",
        "devmind_task_start", "devmind_task_status", "devmind_task_result",
        "devmind_task_cancel", "devmind_task_continue", "devmind_task_list"
    };

    /// <summary>
    /// Filters the tool list based on the profile.
    /// "full" returns all tools as-is.
    /// "restricted" keeps only tools whose name is in the allowlist.
    /// Unknown profile string is treated as "full".
    /// </summary>
    public static IReadOnlyList<ToolDefinition> Filter(IReadOnlyList<ToolDefinition> tools, string profile)
    {
        if (profile == Restricted)
        {
            var filtered = new List<ToolDefinition>();
            foreach (var tool in tools)
            {
                if (RestrictedAllowlist.Contains(tool.Name))
                {
                    filtered.Add(tool);
                }
            }
            return filtered;
        }

        // "full" or any unknown profile → return as-is
        return tools;
    }

    /// <summary>
    /// Checks if a tool name is allowed under the given profile.
    /// Used at execution time for defense-in-depth denial.
    /// </summary>
    public static bool IsToolAllowed(string toolName, string profile)
    {
        if (profile == Restricted)
        {
            return RestrictedAllowlist.Contains(toolName);
        }
        // "full" or any unknown profile → all tools allowed
        return true;
    }

    /// <summary>
    /// Returns a denial message for a tool that was rejected under the restricted profile.
    /// </summary>
    public static string DenyMessage(string toolName)
    {
        return $"[denied] tool '{toolName}' is not available under the restricted profile — delegate via devmind_task_start instead";
    }
}
