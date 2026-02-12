using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Serilog;
using XIVLauncher.Common.Game.Patch.PatchList;
using XIVLauncher.Common.PlatformAbstractions;

namespace XIVLauncher.Common.Game;

public class TaiwanLauncher
{
    private const string LAUNCHER_LOGIN_URL = "https://user.ffxiv.com.tw/api/login/launcherLogin";
    private const string LAUNCHER_SESSION_URL = "https://user.ffxiv.com.tw/api/login/launcherSession";
    private const string PATCH_GAMEVER_URL = "http://patch-gamever.ffxiv.com.tw/http/win32/ffxivtc_release_tc_game/{0}/";

    private readonly HttpClient client;

    public TaiwanLauncher()
    {
        this.client = new HttpClient
        {
            Timeout = TimeSpan.FromMinutes(5),
        };
        this.client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 AppleWebKit/537.36 (KHTML, like Gecko; compatible; Orbit/1.0)");
        this.client.DefaultRequestHeaders.Add("Accept-Language", "en-US, en");
        this.client.DefaultRequestHeaders.Add("Accept", "*/*");
    }

    #region Models

    public class TwLoginResult
    {
        public bool Success { get; set; }
        public string? SessionId { get; set; }
        public string? ErrorMessage { get; set; }
    }

    public enum TwLoginState
    {
        Ok,
        NeedsPatchGame,
    }

    public class TwGameCheckResult
    {
        public TwLoginState State { get; set; }
        public PatchListEntry[] PendingPatches { get; set; } = Array.Empty<PatchListEntry>();
    }

    private class LauncherLoginRequest
    {
        [JsonPropertyName("email")]
        public string Email { get; set; } = "";

        [JsonPropertyName("password")]
        public string Password { get; set; } = "";

        [JsonPropertyName("code")]
        public string Code { get; set; } = "";

        [JsonPropertyName("token")]
        public string Token { get; set; } = "";
    }

    private class LauncherLoginResponse
    {
        [JsonPropertyName("token")]
        public string? Token { get; set; }

        [JsonPropertyName("remain")]
        public int Remain { get; set; }

        [JsonPropertyName("error")]
        public string? Error { get; set; }
    }

    private class LauncherSessionRequest
    {
        [JsonPropertyName("token")]
        public string Token { get; set; } = "";
    }

    private class LauncherSessionResponse
    {
        [JsonPropertyName("sessionId")]
        public string? SessionId { get; set; }

        [JsonPropertyName("error")]
        public string? Error { get; set; }
    }

    #endregion

    private static string ToHex(string input)
    {
        return Convert.ToHexString(Encoding.UTF8.GetBytes(input)).ToLowerInvariant();
    }

    /// <summary>
    /// Log in to the Taiwan FFXIV server.
    /// </summary>
    public async Task<TwLoginResult> LoginAsync(string email, string password, string? otp = null, string? captchaToken = null)
    {
        try
        {
            Log.Information("TaiwanLauncher::LoginAsync starting...");

            // Step 1: launcherLogin - hex-encode credentials
            var loginPayload = new LauncherLoginRequest
            {
                Email = ToHex(email),
                Password = ToHex(password),
                Code = otp ?? "",
                Token = captchaToken ?? "",
            };

            Log.Debug("TaiwanLauncher: Login payload email(hex)={HexEmail}... code={HasCode}",
                loginPayload.Email.Length > 8 ? loginPayload.Email[..8] : loginPayload.Email,
                !string.IsNullOrEmpty(loginPayload.Code));

            // Use PostAsJsonAsync to avoid UTF-8 BOM that StringContent adds
            var loginResponse = await this.client.PostAsJsonAsync(LAUNCHER_LOGIN_URL, loginPayload).ConfigureAwait(false);
            var loginResponseText = await loginResponse.Content.ReadAsStringAsync().ConfigureAwait(false);

            Log.Debug("TaiwanLauncher: Login response {StatusCode}: {Response}", (int)loginResponse.StatusCode, loginResponseText);

            if (!loginResponse.IsSuccessStatusCode)
            {
                Log.Error("Taiwan login failed: {StatusCode} - {Response}", loginResponse.StatusCode, loginResponseText);
                return new TwLoginResult
                {
                    Success = false,
                    ErrorMessage = $"Login failed: {loginResponse.StatusCode} - {loginResponseText}",
                };
            }

            var loginResult = JsonSerializer.Deserialize<LauncherLoginResponse>(loginResponseText);

            if (!string.IsNullOrEmpty(loginResult?.Error))
            {
                Log.Error("Taiwan login error: {Error}", loginResult.Error);
                return new TwLoginResult { Success = false, ErrorMessage = loginResult.Error };
            }

            if (loginResult?.Token == null)
            {
                Log.Error("Taiwan login failed: no token in response: {Response}", loginResponseText);
                return new TwLoginResult { Success = false, ErrorMessage = $"Login failed: {loginResponseText}" };
            }

            Log.Information("TaiwanLauncher: Got login token, requesting session...");

            // Step 2: launcherSession - use PostAsJsonAsync (no BOM)
            var sessionPayload = new LauncherSessionRequest { Token = loginResult.Token };
            var sessionResponse = await this.client.PostAsJsonAsync(LAUNCHER_SESSION_URL, sessionPayload).ConfigureAwait(false);
            var sessionResponseText = await sessionResponse.Content.ReadAsStringAsync().ConfigureAwait(false);

            if (!sessionResponse.IsSuccessStatusCode)
            {
                Log.Error("Taiwan session failed: {StatusCode} - {Response}", sessionResponse.StatusCode, sessionResponseText);
                return new TwLoginResult
                {
                    Success = false,
                    ErrorMessage = $"Session failed: {sessionResponse.StatusCode} - {sessionResponseText}",
                };
            }

            var sessionResult = JsonSerializer.Deserialize<LauncherSessionResponse>(sessionResponseText);

            if (!string.IsNullOrEmpty(sessionResult?.Error))
            {
                Log.Error("Taiwan session error: {Error}", sessionResult.Error);
                return new TwLoginResult { Success = false, ErrorMessage = sessionResult.Error };
            }

            if (sessionResult?.SessionId == null)
            {
                Log.Error("Taiwan session: no sessionId in response: {Response}", sessionResponseText);
                return new TwLoginResult { Success = false, ErrorMessage = $"Failed to get session: {sessionResponseText}" };
            }

            Log.Information("TaiwanLauncher: Login successful, got session ID");
            return new TwLoginResult { Success = true, SessionId = sessionResult.SessionId };
        }
        catch (Exception ex)
        {
            Log.Error(ex, "TaiwanLauncher: Login exception");
            return new TwLoginResult { Success = false, ErrorMessage = ex.Message };
        }
    }

    /// <summary>
    /// Check game version against Taiwan patch server.
    /// Returns pending patches if update is needed.
    /// </summary>
    public async Task<TwGameCheckResult> CheckGameVersionAsync(DirectoryInfo gamePath)
    {
        var gameVer = Repository.Ffxiv.GetVer(gamePath);

        Log.Information("TaiwanLauncher: Checking game version {GameVer}", gameVer);

        // Build request body - Taiwan format: starts with \n (skip boot hash), then expansion versions
        var body = new StringBuilder();
        body.Append("\n"); // TC Region: skip boot version check

        for (int i = 1; i <= 5; i++)
        {
            var repo = i switch
            {
                1 => Repository.Ex1,
                2 => Repository.Ex2,
                3 => Repository.Ex3,
                4 => Repository.Ex4,
                5 => Repository.Ex5,
                _ => throw new ArgumentOutOfRangeException(),
            };

            try
            {
                var ver = repo.GetVer(gamePath);
                if (!string.IsNullOrWhiteSpace(ver) && ver != Constants.BASE_GAME_VERSION)
                {
                    body.Append($"ex{i}\t{ver}\n");
                }
            }
            catch
            {
                // Expansion not installed, skip
            }
        }

        var url = string.Format(PATCH_GAMEVER_URL, gameVer);
        var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.Add("X-Hash-Check", "enabled");
        request.Content = new ByteArrayContent(Encoding.UTF8.GetBytes(body.ToString()));

        var response = await this.client.SendAsync(request).ConfigureAwait(false);

        // 204 No Content = no patches needed
        if (response.StatusCode == HttpStatusCode.NoContent)
        {
            Log.Information("TaiwanLauncher: Game is up to date");
            return new TwGameCheckResult { State = TwLoginState.Ok };
        }

        var responseBody = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

        // Check if there are actual patch URLs
        if (!responseBody.Contains("http://patch-dl.ffxiv.com.tw"))
        {
            Log.Information("TaiwanLauncher: No patches in response body");
            return new TwGameCheckResult { State = TwLoginState.Ok };
        }

        // Parse Taiwan patch format (multipart response)
        var patches = ParseTaiwanPatchResponse(responseBody);

        if (patches.Length == 0)
        {
            return new TwGameCheckResult { State = TwLoginState.Ok };
        }

        Log.Information("TaiwanLauncher: {PatchCount} patches needed", patches.Length);
        return new TwGameCheckResult { State = TwLoginState.NeedsPatchGame, PendingPatches = patches };
    }

    /// <summary>
    /// Parse Taiwan version check API response into PatchListEntry array.
    /// Format: multipart/mixed with tab-separated fields:
    /// size, totalSize, count, parts, version, hashType, blockSize, hashes, url
    /// </summary>
    private static PatchListEntry[] ParseTaiwanPatchResponse(string response)
    {
        var patches = new List<PatchListEntry>();

        foreach (var line in response.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmedLine = line.Trim();

            // Skip multipart boundary and header lines
            if (trimmedLine.StartsWith("--") ||
                trimmedLine.StartsWith("Content-") ||
                trimmedLine.StartsWith("X-") ||
                string.IsNullOrWhiteSpace(trimmedLine))
            {
                continue;
            }

            var parts = trimmedLine.Split('\t');
            if (parts.Length < 9) continue;

            var url = parts[8];
            if (!url.StartsWith("http://")) continue;

            try
            {
                patches.Add(new PatchListEntry
                {
                    Length = long.Parse(parts[0]),
                    VersionId = parts[4],
                    HashType = parts[5],
                    HashBlockSize = long.Parse(parts[6]),
                    Hashes = parts[7].Split(','),
                    Url = url,
                });
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "TaiwanLauncher: Failed to parse patch line: {Line}", trimmedLine);
            }
        }

        return patches.ToArray();
    }

    /// <summary>
    /// Launch the game with Taiwan server arguments.
    /// Taiwan uses plain-text arguments (no SE encryption).
    /// </summary>
    public Process? LaunchGame(IGameRunner runner, string sessionId, DirectoryInfo gamePath, DpiAwareness dpiAwareness, string additionalArguments = "")
    {
        var exePath = Path.Combine(gamePath.FullName, "game", "ffxiv_dx11.exe");
        var workingDir = Path.Combine(gamePath.FullName, "game");
        var environment = new Dictionary<string, string>();

        if (!File.Exists(exePath))
            throw new FileNotFoundException($"Game executable not found: {exePath}");

        // Taiwan launch arguments - plain text, no encryption
        var args = string.Join(" ",
            "DEV.LobbyHost01=neolobby01.ffxiv.com.tw",
            "DEV.LobbyPort01=54994",
            "DEV.GMServerHost=frontier.ffxiv.com.tw",
            $"DEV.TestSID={sessionId}",
            "SYS.resetConfig=0",
            "DEV.SaveDataBankHost=config-dl.ffxiv.com.tw");

        if (!string.IsNullOrEmpty(additionalArguments))
        {
            args += " " + additionalArguments.Trim();
        }

        Log.Information("TaiwanLauncher: Launching game at {ExePath}", exePath);

        return runner.Start(exePath, workingDir, args, environment, dpiAwareness);
    }
}
