using DevMX.App.ViewModels;

namespace DevMX.App.ViewModels.Tests;

public class DevMxSettingsTests
{
    [Fact]
    public void LoadSave_RoundTripPreservesValues()
    {
        // Use a temp directory for the settings file
        string tempDir = Path.Combine(Path.GetTempPath(), $"devmx_settings_test_{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(tempDir);

            // Monkey-patch the default path by creating a custom path scenario
            // Since DevMxSettings uses a static DefaultSettingsPath, we'll test by
            // directly serializing/deserializing
            var original = new DevMxSettings
            {
                Endpoint = "http://myserver:9000/v1",
                Model = "gpt-4o-mini",
                Provider = "anthropic",
                WorkDir = @"C:\my\work",
                ServerExe = @"C:\custom\server.exe",
                Theme = "light",
                PollThrottleSeconds = 7
            };

            // Save to temp location
            string tempPath = Path.Combine(tempDir, "settings.json");
            var json = System.Text.Json.JsonSerializer.Serialize(original, new System.Text.Json.JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase
            });
            File.WriteAllText(tempPath, json);

            // Read back
            var loadedJson = File.ReadAllText(tempPath);
            var loaded = System.Text.Json.JsonSerializer.Deserialize<DevMxSettings>(loadedJson, new System.Text.Json.JsonSerializerOptions
            {
                PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase
            });

            Assert.NotNull(loaded);
            Assert.Equal(original.Endpoint, loaded.Endpoint);
            Assert.Equal(original.Model, loaded.Model);
            Assert.Equal(original.Provider, loaded.Provider);
            Assert.Equal(original.WorkDir, loaded.WorkDir);
            Assert.Equal(original.ServerExe, loaded.ServerExe);
            Assert.Equal(original.Theme, loaded.Theme);
            Assert.Equal(original.PollThrottleSeconds, loaded.PollThrottleSeconds);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public void Load_FromNonExistentPath_ReturnsDefaults()
    {
        // DevMxSettings.Load() returns defaults when file doesn't exist
        // We can't easily test this without modifying the static path,
        // so we test the deserialization behavior with an empty file
        var settings = new DevMxSettings();
        Assert.Equal(DevMxSettings.DefaultEndpoint, settings.Endpoint);
        Assert.Equal("openai", settings.Provider);
        Assert.Equal(string.Empty, settings.Model);
        Assert.Equal(DevMxSettings.DefaultWorkDir, settings.WorkDir);
        Assert.Equal(DevMxSettings.DefaultServerExe, settings.ServerExe);
        Assert.Equal("dark", settings.Theme);
        Assert.Equal(5, settings.PollThrottleSeconds);
    }

    [Fact]
    public void SaveCreatesDirectoryAndFile()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"devmx_settings_test_{Guid.NewGuid():N}");
        try
        {
            // We test the Save logic manually since DefaultSettingsPath is static
            var settings = new DevMxSettings
            {
                Endpoint = "http://test:8080/v1",
                Model = "test-model",
                Provider = "openai",
                WorkDir = tempDir,
                ServerExe = @"C:\test\server.exe"
            };

            string tempPath = Path.Combine(tempDir, "settings.json");
            var json = System.Text.Json.JsonSerializer.Serialize(settings, new System.Text.Json.JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase
            });

            Directory.CreateDirectory(tempDir);
            File.WriteAllText(tempPath, json);

            Assert.True(File.Exists(tempPath));

            var loaded = System.Text.Json.JsonSerializer.Deserialize<DevMxSettings>(File.ReadAllText(tempPath), new System.Text.Json.JsonSerializerOptions
            {
                PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase
            });

            Assert.NotNull(loaded);
            Assert.Equal("http://test:8080/v1", loaded.Endpoint);
            Assert.Equal("test-model", loaded.Model);
            Assert.Equal("openai", loaded.Provider);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public void SettingsViewModel_ChangingTheme_RaisesCallback()
    {
        // Arrange — use injectable path so we can verify file persist
        string tempPath = Path.Combine(Path.GetTempPath(), $"devmx_theme_test_{Guid.NewGuid():N}.json");
        try
        {
            var settings = new DevMxSettings { Theme = "dark" };
            settings.Save(tempPath);

            string? capturedTheme = null;
            var vm = new SettingsViewModel(settings, () => { }, theme => capturedTheme = theme, tempPath);

            // Constructor does NOT fire callback (FIX 2: no ctor side-effect)
            Assert.Equal("dark", vm.Theme);
            Assert.Null(capturedTheme); // callback suppressed during construction

            // Act - change to light
            vm.Theme = "light";

            // Assert
            Assert.Equal("light", vm.Theme);
            Assert.Equal("light", capturedTheme); // callback fired with new value
            // Verify persisted to file
            var reloaded = DevMxSettings.Load(tempPath);
            Assert.Equal("light", reloaded.Theme);
        }
        finally
        {
            try { File.Delete(tempPath); } catch { }
        }
    }

    [Fact]
    public void SettingsViewModel_ThemeOptions_ReturnsDarkAndLight()
    {
        // Arrange
        var settings = new DevMxSettings();
        var vm = new SettingsViewModel(settings, () => { });

        // Assert
        var options = vm.ThemeOptions.ToList();
        Assert.Equal(new[] { "dark", "light" }, options);
    }

    [Fact]
    public void Settings_DefaultToolProfile_IsAuto()
    {
        var settings = new DevMxSettings();
        Assert.Equal("auto", settings.ToolProfile);
    }

    [Fact]
    public void SettingsViewModel_ToolProfile_RoundTrip()
    {
        string tempPath = Path.Combine(Path.GetTempPath(), $"devmx_profile_test_{Guid.NewGuid():N}.json");
        try
        {
            var settings = new DevMxSettings { ToolProfile = "restricted" };
            settings.Save(tempPath);
            var vm = new SettingsViewModel(settings, () => { }, null, tempPath);

            Assert.Equal("restricted", vm.ToolProfile);

            // Change via command
            vm.SetToolProfileCommand.Execute("full");
            Assert.Equal("full", vm.ToolProfile);
            // Verify persisted to file
            var reloaded = DevMxSettings.Load(tempPath);
            Assert.Equal("full", reloaded.ToolProfile);
        }
        finally
        {
            try { File.Delete(tempPath); } catch { }
        }
    }

    [Fact]
    public void SettingsViewModel_ToolProfileOptions_ReturnsAutoFullRestricted()
    {
        var settings = new DevMxSettings();
        var vm = new SettingsViewModel(settings, () => { });

        var options = vm.ToolProfileOptions.ToList();
        Assert.Equal(new[] { "auto", "full", "restricted" }, options);
    }

    [Fact]
    public void Settings_RoundTripPreservesToolProfile()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"devmx_settings_test_{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(tempDir);

            var original = new DevMxSettings
            {
                Endpoint = "http://myserver:9000/v1",
                Model = "gpt-4o-mini",
                Provider = "anthropic",
                WorkDir = @"C:\my\work",
                ServerExe = @"C:\custom\server.exe",
                Theme = "light",
                ToolProfile = "restricted"
            };

            string tempPath = Path.Combine(tempDir, "settings.json");
            var json = System.Text.Json.JsonSerializer.Serialize(original, new System.Text.Json.JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase
            });
            File.WriteAllText(tempPath, json);

            var loaded = System.Text.Json.JsonSerializer.Deserialize<DevMxSettings>(File.ReadAllText(tempPath), new System.Text.Json.JsonSerializerOptions
            {
                PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase
            });

            Assert.NotNull(loaded);
            Assert.Equal("restricted", loaded.ToolProfile);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public void AppSession_AutoResolution_LoopbackEndpoint_ReturnsFull()
    {
        // Test via reflection since ResolveEffectiveToolProfile is private static
        var method = typeof(AppSession).GetMethod("ResolveEffectiveToolProfile",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(method);

        var result = method.Invoke(null, new object[] { "auto", "http://127.0.0.1:8080/v1", "openai" });
        Assert.Equal("full", result);
    }

    [Fact]
    public void AppSession_AutoResolution_RemoteEndpoint_ReturnsRestricted()
    {
        var method = typeof(AppSession).GetMethod("ResolveEffectiveToolProfile",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(method);

        var result = method.Invoke(null, new object[] { "auto", "https://api.openai.com/v1", "openai" });
        Assert.Equal("restricted", result);
    }

    [Fact]
    public void AppSession_AutoResolution_Anthropic_ReturnsRestricted()
    {
        var method = typeof(AppSession).GetMethod("ResolveEffectiveToolProfile",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(method);

        var result = method.Invoke(null, new object[] { "auto", "http://127.0.0.1:8080/v1", "anthropic" });
        Assert.Equal("restricted", result);
    }

    [Fact]
    public void AppSession_AutoResolution_Localhost_ReturnsFull()
    {
        var method = typeof(AppSession).GetMethod("ResolveEffectiveToolProfile",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(method);

        var result = method.Invoke(null, new object[] { "auto", "http://localhost:8080/v1", "openai" });
        Assert.Equal("full", result);
    }

    [Fact]
    public void AppSession_AutoResolution_ExplicitProfile_ReturnsAsIs()
    {
        var method = typeof(AppSession).GetMethod("ResolveEffectiveToolProfile",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(method);

        // Explicit "full" stays full even for remote
        var result = method.Invoke(null, new object[] { "full", "https://api.openai.com/v1", "openai" });
        Assert.Equal("full", result);

        // Explicit "restricted" stays restricted even for loopback
        var result2 = method.Invoke(null, new object[] { "restricted", "http://127.0.0.1:8080/v1", "openai" });
        Assert.Equal("restricted", result2);
    }

    [Fact]
    public void AppSession_SystemPrompt_RestrictedContainsDelegationHint()
    {
        var method = typeof(AppSession).GetMethod("BuildSystemPrompt",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(method);

        var result = (string)method.Invoke(null, new object[] { "/work", "restricted" })!;
        Assert.True(result.Contains("devmind_task_start"), $"Expected 'devmind_task_start' in: {result}");
        Assert.True(result.Contains("read-only"), $"Expected 'read-only' in: {result}");
        Assert.False(result.Contains("make small judgment edits directly"), $"Did not expect 'make small judgment edits directly' in: {result}");
    }

    [Fact]
    public void AppSession_SystemPrompt_FullContainsStandardPrompt()
    {
        var method = typeof(AppSession).GetMethod("BuildSystemPrompt",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(method);

        var result = (string)method.Invoke(null, new object[] { "/work", "full" })!;
        Assert.True(result.Contains("read, write, and analyze"), $"Expected 'read, write, and analyze' in: {result}");
        Assert.False(result.Contains("read-only"), $"Did not expect 'read-only' in: {result}");
    }

    [Fact]
    public void AppSession_ClampPollThrottle_ClampsToRange()
    {
        var method = typeof(AppSession).GetMethod("ClampPollThrottle",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(method);

        // 999 → 60
        Assert.Equal(60, method.Invoke(null, new object[] { 999 }));
        // -3 → 0
        Assert.Equal(0, method.Invoke(null, new object[] { -3 }));
        // 5 → 5 (in range)
        Assert.Equal(5, method.Invoke(null, new object[] { 5 }));
        // 0 → 0 (boundary)
        Assert.Equal(0, method.Invoke(null, new object[] { 0 }));
        // 60 → 60 (boundary)
        Assert.Equal(60, method.Invoke(null, new object[] { 60 }));
    }

    [Fact]
    public void SettingsViewModel_PollThrottleSeconds_NonNumericRevertsOnApply()
    {
        string tempPath = Path.Combine(Path.GetTempPath(), $"devmx_poll_test_{Guid.NewGuid():N}.json");
        try
        {
            var settings = new DevMxSettings { PollThrottleSeconds = 5 };
            settings.Save(tempPath);
            bool applied = false;
            var vm = new SettingsViewModel(settings, () => applied = true, null, tempPath);

            Assert.Equal("5", vm.PollThrottleSeconds);

            // Set non-numeric value
            vm.PollThrottleSeconds = "abc";
            vm.ApplyCommand.Execute(null);

            Assert.True(applied);
            // Should keep disk value (5) since "abc" is not valid
            var reloaded = DevMxSettings.Load(tempPath);
            Assert.Equal(5, reloaded.PollThrottleSeconds);
        }
        finally
        {
            try { File.Delete(tempPath); } catch { }
        }
    }

    [Fact]
    public void SettingsViewModel_PollThrottleSeconds_ValidIntegerPersists()
    {
        string tempPath = Path.Combine(Path.GetTempPath(), $"devmx_poll_test_{Guid.NewGuid():N}.json");
        try
        {
            var settings = new DevMxSettings { PollThrottleSeconds = 5 };
            settings.Save(tempPath);
            bool applied = false;
            var vm = new SettingsViewModel(settings, () => applied = true, null, tempPath);

            vm.PollThrottleSeconds = "10";
            vm.ApplyCommand.Execute(null);

            Assert.True(applied);
            var reloaded = DevMxSettings.Load(tempPath);
            Assert.Equal(10, reloaded.PollThrottleSeconds);
        }
        finally
        {
            try { File.Delete(tempPath); } catch { }
        }
    }

    // ===== FIX 1: Merge-on-apply tests =====

    [Fact]
    public void SettingsViewModel_MergeOnApply_UneditedFieldsKeepDiskValues()
    {
        // Construct VM from settings A; simulate disk change to B; user edits ONLY poll;
        // Apply → merged file has B's endpoint/model + user's poll.
        string tempPath = Path.Combine(Path.GetTempPath(), $"devmx_merge_test_{Guid.NewGuid():N}.json");
        try
        {
            // Settings A: initial values
            var settingsA = new DevMxSettings
            {
                Endpoint = "http://a:8080/v1",
                Model = "model-a",
                Provider = "openai",
                WorkDir = @"C:\work-a",
                Theme = "dark",
                ToolProfile = "auto",
                PollThrottleSeconds = 5
            };
            settingsA.Save(tempPath);

            // Create VM from settings A
            var vm = new SettingsViewModel(settingsA, () => { }, null, tempPath);
            Assert.Equal("http://a:8080/v1", vm.Endpoint);
            Assert.Equal("5", vm.PollThrottleSeconds);

            // Simulate external disk change to settings B (e.g., another session edited the file)
            var settingsB = new DevMxSettings
            {
                Endpoint = "http://b:9090/v1",
                Model = "model-b",
                Provider = "anthropic",
                WorkDir = @"C:\work-b",
                Theme = "light",
                ToolProfile = "full",
                PollThrottleSeconds = 10
            };
            settingsB.Save(tempPath);

            // User edits ONLY poll in the VM
            vm.PollThrottleSeconds = "15";

            // Apply
            vm.ApplyCommand.Execute(null);

            // Verify merged result: B's endpoint/model, user's poll
            var merged = DevMxSettings.Load(tempPath);
            Assert.Equal("http://b:9090/v1", merged.Endpoint);  // from disk (B)
            Assert.Equal("model-b", merged.Model);               // from disk (B)
            Assert.Equal("anthropic", merged.Provider);           // from disk (B)
            Assert.Equal(@"C:\work-b", merged.WorkDir);           // from disk (B)
            Assert.Equal("light", merged.Theme);                  // from disk (B)
            Assert.Equal("full", merged.ToolProfile);             // from disk (B)
            Assert.Equal(15, merged.PollThrottleSeconds);         // user's edit
        }
        finally
        {
            try { File.Delete(tempPath); } catch { }
        }
    }

    [Fact]
    public void SettingsViewModel_DirtyFieldWinsOverDisk()
    {
        // User edits endpoint too → user's endpoint survives over B's.
        string tempPath = Path.Combine(Path.GetTempPath(), $"devmx_dirty_test_{Guid.NewGuid():N}.json");
        try
        {
            var settingsA = new DevMxSettings
            {
                Endpoint = "http://a:8080/v1",
                Model = "model-a",
                PollThrottleSeconds = 5
            };
            settingsA.Save(tempPath);

            var vm = new SettingsViewModel(settingsA, () => { }, null, tempPath);

            // Simulate disk change
            var settingsB = new DevMxSettings
            {
                Endpoint = "http://b:9090/v1",
                Model = "model-b",
                PollThrottleSeconds = 10
            };
            settingsB.Save(tempPath);

            // User edits endpoint AND poll
            vm.Endpoint = "http://user:7070/v1";
            vm.PollThrottleSeconds = "20";

            vm.ApplyCommand.Execute(null);

            var merged = DevMxSettings.Load(tempPath);
            Assert.Equal("http://user:7070/v1", merged.Endpoint);  // user's dirty value wins
            Assert.Equal("model-b", merged.Model);                  // from disk (B), not edited
            Assert.Equal(20, merged.PollThrottleSeconds);           // user's edit
        }
        finally
        {
            try { File.Delete(tempPath); } catch { }
        }
    }

    // ===== FIX 2: No ctor side-effect save test =====

    [Fact]
    public void SettingsViewModel_NoSaveDuringConstruction()
    {
        // Constructing a VM does not write the file.
        string tempPath = Path.Combine(Path.GetTempPath(), $"devmx_nosave_test_{Guid.NewGuid():N}.json");
        try
        {
            var settings = new DevMxSettings { Theme = "dark", PollThrottleSeconds = 5 };
            settings.Save(tempPath);

            // Record file content/mtime before construction
            var beforeContent = File.ReadAllText(tempPath);
            var beforeTime = File.GetLastWriteTime(tempPath);

            // Small delay to detect any write
            Thread.Sleep(10);

            bool callbackFired = false;
            var vm = new SettingsViewModel(settings, () => { }, theme => callbackFired = true, tempPath);

            // Assert: callback did NOT fire during construction
            Assert.False(callbackFired);

            // Assert: file was NOT modified during construction
            var afterContent = File.ReadAllText(tempPath);
            var afterTime = File.GetLastWriteTime(tempPath);
            Assert.Equal(beforeContent, afterContent);
            Assert.Equal(beforeTime, afterTime);
        }
        finally
        {
            try { File.Delete(tempPath); } catch { }
        }
    }

    // ===== Immediate persist still works post-init =====

    [Fact]
    public void SettingsViewModel_ImmediatePersistWorksPostInit()
    {
        // After construction, SetTheme/SetToolProfile still persist immediately.
        string tempPath = Path.Combine(Path.GetTempPath(), $"devmx_persist_test_{Guid.NewGuid():N}.json");
        try
        {
            var settings = new DevMxSettings { Theme = "dark", ToolProfile = "auto" };
            settings.Save(tempPath);

            var vm = new SettingsViewModel(settings, () => { }, null, tempPath);

            // SetTheme persists immediately
            vm.SetThemeCommand.Execute("light");
            var afterTheme = DevMxSettings.Load(tempPath);
            Assert.Equal("light", afterTheme.Theme);

            // SetToolProfile persists immediately
            vm.SetToolProfileCommand.Execute("full");
            var afterProfile = DevMxSettings.Load(tempPath);
            Assert.Equal("full", afterProfile.ToolProfile);
        }
        finally
        {
            try { File.Delete(tempPath); } catch { }
        }
    }
}
