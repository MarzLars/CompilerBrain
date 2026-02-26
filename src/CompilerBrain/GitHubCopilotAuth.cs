using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace CompilerBrain;

// Handles GitHub OAuth device flow and Copilot token exchange.
public static class GitHubCopilotAuth
{
    const string ClientId = "Iv1.b507a08c87ecfe98";
    const string Scope = "read:user";
    const string DeviceCodeUrl = "https://github.com/login/device/code";
    const string AccessTokenUrl = "https://github.com/login/oauth/access_token";
    const string CopilotTokenUrl = "https://api.github.com/copilot_internal/v2/token";

    static readonly string TokenCachePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CompilerBrain",
        "github_oauth_token.json");

    // Gets a valid Copilot session token, performing OAuth device flow if needed.
    public static (string Token, string ApiBaseUrl) GetCopilotToken()
    {
        var oauthToken = LoadCachedOAuthToken() ?? RunDeviceFlow();
        return ExchangeForCopilotToken(oauthToken);
    }

    static string? LoadCachedOAuthToken()
    {
        if (!File.Exists(TokenCachePath))
            return null;

        try
        {
            var json = File.ReadAllText(TokenCachePath);
            var doc = JsonDocument.Parse(json);
            return doc.RootElement.GetProperty("access_token").GetString();
        }
        catch
        {
            return null;
        }
    }

    static void SaveOAuthToken(string accessToken)
    {
        var dir = Path.GetDirectoryName(TokenCachePath)!;
        Directory.CreateDirectory(dir);

        var json = JsonSerializer.Serialize(new { access_token = accessToken });
        File.WriteAllText(TokenCachePath, json);
    }

    static string RunDeviceFlow()
    {
        using var http = new HttpClient();
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        // Step 1: Request device code
        var body = $"client_id={ClientId}&scope={Scope}";
        var content = new StringContent(body, Encoding.UTF8, "application/x-www-form-urlencoded");
        var response = http.PostAsync(DeviceCodeUrl, content).Result;
        response.EnsureSuccessStatusCode();

        var responseJson = response.Content.ReadAsStringAsync().Result;
        var deviceDoc = JsonDocument.Parse(responseJson);

        var deviceCode = deviceDoc.RootElement.GetProperty("device_code").GetString()!;
        var userCode = deviceDoc.RootElement.GetProperty("user_code").GetString()!;
        var verificationUri = deviceDoc.RootElement.GetProperty("verification_uri").GetString()!;
        var interval = deviceDoc.RootElement.TryGetProperty("interval", out var intervalProp)
            ? intervalProp.GetInt32()
            : 5;
        var expiresIn = deviceDoc.RootElement.TryGetProperty("expires_in", out var expiresProp)
            ? expiresProp.GetInt32()
            : 900;

        // Step 2: Show user the code
        Console.WriteLine();
        Console.WriteLine("=== GitHub Copilot Authentication ===");
        Console.WriteLine($"  1. Open: {verificationUri}");
        Console.WriteLine($"  2. Enter code: {userCode}");
        Console.WriteLine();
        Console.WriteLine("Waiting for authorization...");

        // Step 3: Poll for access token
        var deadline = DateTime.UtcNow.AddSeconds(expiresIn);
        while (DateTime.UtcNow < deadline)
        {
            Thread.Sleep(interval * 1000);

            var pollBody = $"client_id={ClientId}&device_code={deviceCode}&grant_type=urn:ietf:params:oauth:grant-type:device_code";
            var pollContent = new StringContent(pollBody, Encoding.UTF8, "application/x-www-form-urlencoded");
            var pollResponse = http.PostAsync(AccessTokenUrl, pollContent).Result;
            var pollJson = pollResponse.Content.ReadAsStringAsync().Result;
            var pollDoc = JsonDocument.Parse(pollJson);

            if (pollDoc.RootElement.TryGetProperty("access_token", out var tokenProp))
            {
                var accessToken = tokenProp.GetString()!;
                SaveOAuthToken(accessToken);
                Console.WriteLine("Authenticated successfully!");
                return accessToken;
            }

            if (pollDoc.RootElement.TryGetProperty("error", out var errorProp))
            {
                var error = errorProp.GetString();
                switch (error)
                {
                    case "authorization_pending":
                        continue;
                    case "slow_down":
                        interval += 2;
                        continue;
                    case "expired_token":
                        throw new Exception("Device code expired. Please restart and try again.");
                    case "access_denied":
                        throw new Exception("Authorization was denied by the user.");
                    default:
                        throw new Exception($"OAuth error: {error}");
                }
            }
        }

        throw new Exception("Device code expired before authorization was completed.");
    }

    static (string Token, string ApiBaseUrl) ExchangeForCopilotToken(string oauthToken)
    {
        using var http = new HttpClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", oauthToken);
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("CompilerBrain", "1.0"));

        var response = http.GetAsync(CopilotTokenUrl).Result;

        if (!response.IsSuccessStatusCode)
        {
            // OAuth token might be expired/revoked — clear cache and retry
            if (File.Exists(TokenCachePath))
                File.Delete(TokenCachePath);

            throw new Exception(
                $"Failed to get Copilot token (HTTP {(int)response.StatusCode}). " +
                "Your GitHub OAuth token may have been revoked. Please restart to re-authenticate.");
        }

        var json = response.Content.ReadAsStringAsync().Result;
        var doc = JsonDocument.Parse(json);
        var copilotToken = doc.RootElement.GetProperty("token").GetString()!;

        // Derive API base URL from token (may contain proxy-ep= field)
        var apiBaseUrl = "https://api.githubcopilot.com";
        var parts = copilotToken.Split(';');
        foreach (var part in parts)
        {
            var trimmed = part.Trim();
            if (trimmed.StartsWith("proxy-ep="))
            {
                var candidate = trimmed["proxy-ep=".Length..];
                if (Uri.TryCreate(candidate, UriKind.Absolute, out _))
                {
                    apiBaseUrl = candidate;
                }
                break;
            }
        }

        return (copilotToken, apiBaseUrl);
    }
}