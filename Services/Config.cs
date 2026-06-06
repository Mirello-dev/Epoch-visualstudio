using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.Serialization;
using System.Threading.Tasks;
using System.Windows.Forms;
using EpochVisualStudio.UI;

namespace EpochVisualStudio.Services
{
    [DataContract]
    internal class EpochConfig
    {
        [DataMember(Name = "apiKey", EmitDefaultValue = false)]
        public string ApiKey { get; set; }

        [DataMember(Name = "baseUrl", EmitDefaultValue = false)]
        public string BaseUrl { get; set; }
    }

    [DataContract]
    internal class ValidateResponse
    {
        [DataMember(Name = "data")]
        public ValidateData Data { get; set; }
    }

    [DataContract]
    internal class ValidateData
    {
        [DataMember(Name = "valid")]
        public bool Valid { get; set; }
    }

    /// <summary>
    /// Reads and writes the shared epoch configuration. The config lives at
    /// <c>$XDG_CONFIG_HOME/epoch/config.json</c> (falling back to
    /// <c>~/.config/epoch/config.json</c>) — the exact same location and JSON
    /// shape used by the VS Code extension, so a user running both editors shares
    /// one API key and instance URL.
    /// </summary>
    internal static class Config
    {
        public const string DefaultBaseUrl = "https://epoch.mirello.cloud";

        private static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };

        public static string ConfigDir
        {
            get
            {
                var xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
                if (!string.IsNullOrEmpty(xdg))
                {
                    return Path.Combine(xdg, "epoch");
                }
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "epoch");
            }
        }

        private static string ConfigFilePath => Path.Combine(ConfigDir, "config.json");

        private static string LegacyJsonPath =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".epoch.json");

        private static string LegacyCfgPath =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".epoch.cfg");

        public static void EnsureConfigDir()
        {
            try
            {
                Directory.CreateDirectory(ConfigDir);
            }
            catch (Exception ex)
            {
                Logger.Error("Error creating config directory: " + ex.Message);
            }
        }

        /// <summary>
        /// Ensures the config directory exists and migrates any legacy config
        /// files from older epoch clients. Safe to call repeatedly.
        /// </summary>
        public static void EnsureInitialized()
        {
            EnsureConfigDir();
            MigrateLegacyConfigs();
        }

        private static void MigrateLegacyConfigs()
        {
            if (File.Exists(ConfigFilePath))
            {
                return;
            }

            EpochConfig migrated = null;
            string source = null;

            try
            {
                if (File.Exists(LegacyJsonPath))
                {
                    migrated = JsonUtil.Deserialize<EpochConfig>(File.ReadAllText(LegacyJsonPath));
                    source = LegacyJsonPath;
                }
                else if (File.Exists(LegacyCfgPath))
                {
                    migrated = new EpochConfig();
                    foreach (var raw in File.ReadAllLines(LegacyCfgPath))
                    {
                        var line = raw.Trim();
                        if (line.StartsWith("api_key"))
                        {
                            migrated.ApiKey = ValueAfterEquals(line);
                        }
                        else if (line.StartsWith("base_url"))
                        {
                            migrated.BaseUrl = ValueAfterEquals(line)?.Replace("\\:", ":");
                        }
                    }
                    source = LegacyCfgPath;
                }
            }
            catch (Exception ex)
            {
                Logger.Error("Error reading legacy config for migration: " + ex.Message);
                return;
            }

            if (source == null || migrated == null)
            {
                return;
            }

            try
            {
                WriteConfig(migrated);
                try { File.Delete(source); } catch { /* best effort */ }
                Logger.Log("Migrated epoch configuration from " + source + " to " + ConfigFilePath);
            }
            catch (Exception ex)
            {
                Logger.Error("Failed to migrate epoch configuration: " + ex.Message);
            }
        }

        private static string ValueAfterEquals(string line)
        {
            var idx = line.IndexOf('=');
            return idx >= 0 ? line.Substring(idx + 1).Trim() : null;
        }

        public static EpochConfig ReadConfig()
        {
            try
            {
                if (File.Exists(ConfigFilePath))
                {
                    return JsonUtil.Deserialize<EpochConfig>(File.ReadAllText(ConfigFilePath)) ?? new EpochConfig();
                }
            }
            catch (Exception ex)
            {
                Logger.Error("Error reading config file: " + ex.Message);
            }
            return new EpochConfig();
        }

        public static void WriteConfig(EpochConfig config)
        {
            try
            {
                EnsureConfigDir();
                File.WriteAllText(ConfigFilePath, JsonUtil.Serialize(config));
                Logger.Log("Config file updated (" + ConfigFilePath + ")");
            }
            catch (Exception ex)
            {
                Logger.Error("Failed to write config file: " + ex.Message);
            }
        }

        public static string GetApiKey()
        {
            return ReadConfig().ApiKey;
        }

        public static string GetBaseUrl()
        {
            var url = ReadConfig().BaseUrl;
            return string.IsNullOrEmpty(url) ? DefaultBaseUrl : url;
        }

        /// <summary>Prompts for and stores the API key. Must be called on the UI thread.</summary>
        public static void SetApiKey()
        {
            var apiKey = InputBox.Show("epoch: Set API Key", "Enter your epoch API key", string.Empty, password: true);
            if (apiKey == null)
            {
                Logger.Log("API key setting cancelled");
                return;
            }

            var config = ReadConfig();
            config.ApiKey = apiKey;
            WriteConfig(config);
            MessageBox.Show("epoch API key has been updated.", "epoch", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        /// <summary>Prompts for and stores the instance URL. Must be called on the UI thread.</summary>
        public static void SetBaseUrl()
        {
            var current = GetBaseUrl();
            var baseUrl = InputBox.Show("epoch: Set Instance", "Enter your epoch instance URL", current);
            if (baseUrl == null)
            {
                Logger.Log("Base URL setting cancelled");
                return;
            }

            var config = ReadConfig();
            config.BaseUrl = baseUrl;
            WriteConfig(config);
            MessageBox.Show("epoch instance URL has been updated.", "epoch", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        public enum ApiKeyStatus
        {
            Valid,
            Invalid,
            Unreachable
        }

        /// <summary>
        /// Verifies the stored API key against the configured instance by calling
        /// the anonymous <c>/api/validate-key</c> endpoint, which always returns
        /// 200 and carries validity in the response body.
        /// </summary>
        public static async Task<(ApiKeyStatus Status, string Message)> CheckApiKeyAsync(string apiKey, string baseUrl)
        {
            Uri url;
            try
            {
                url = new Uri(new Uri(baseUrl), "/api/validate-key");
            }
            catch (Exception ex)
            {
                return (ApiKeyStatus.Unreachable, "Invalid instance URL: " + ex.Message);
            }

            try
            {
                using (var request = new HttpRequestMessage(HttpMethod.Get, url))
                {
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
                    using (var response = await Http.SendAsync(request).ConfigureAwait(false))
                    {
                        if (!response.IsSuccessStatusCode)
                        {
                            return (ApiKeyStatus.Unreachable, "Server responded with status code " + (int)response.StatusCode);
                        }

                        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                        try
                        {
                            var parsed = JsonUtil.Deserialize<ValidateResponse>(body);
                            var valid = parsed?.Data?.Valid == true;
                            return (valid ? ApiKeyStatus.Valid : ApiKeyStatus.Invalid, null);
                        }
                        catch
                        {
                            return (ApiKeyStatus.Unreachable, "Could not parse server response.");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                return (ApiKeyStatus.Unreachable, ex.Message);
            }
        }

        /// <summary>
        /// Validates the configured API key and reports the result to the user.
        /// The HTTP call runs off the UI thread; only the message boxes touch UI.
        /// </summary>
        public static async Task ValidateApiKeyAsync()
        {
            var apiKey = GetApiKey();
            if (string.IsNullOrEmpty(apiKey))
            {
                MessageBox.Show(
                    "No epoch API key is configured. Run 'epoch: Set API Key' first.",
                    "epoch", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            var baseUrl = GetBaseUrl();
            Logger.Log("Validating API key against " + baseUrl + "...");

            var result = await CheckApiKeyAsync(apiKey, baseUrl).ConfigureAwait(false);

            switch (result.Status)
            {
                case ApiKeyStatus.Valid:
                    Logger.Log("API key is valid for " + baseUrl);
                    MessageBox.Show("epoch API key is valid for " + baseUrl, "epoch",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                    break;
                case ApiKeyStatus.Invalid:
                    Logger.Log("API key is invalid for " + baseUrl);
                    MessageBox.Show(
                        "epoch API key is not valid for " + baseUrl + ". Run 'epoch: Set API Key' to update it.",
                        "epoch", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    break;
                default:
                    Logger.Log("Could not validate API key against " + baseUrl + ": " + result.Message);
                    MessageBox.Show("Could not reach epoch instance at " + baseUrl + ": " + result.Message,
                        "epoch", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    break;
            }
        }
    }
}
