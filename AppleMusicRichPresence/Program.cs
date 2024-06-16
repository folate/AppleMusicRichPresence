using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using DiscordRPC;
using DotNetEnv;

namespace AppleMusicRichPresence;

internal class Program
{
    private static string DiscordAppId {
        get
        {
            Env.Load();
            return Environment.GetEnvironmentVariable("DISCORD_CLIENT_ID");
        }
    }

    private static DiscordRpcClient _client = new(DiscordAppId);
    
    private static string _currentTrackId = "";

    private static readonly HttpClient HttpClient = new()
    {
        DefaultRequestHeaders = { { "User-Agent", "Mozilla/5.0 (Windows NT 10.0; rv:125.0) Gecko/20100101 Firefox/125.0" } }
    };
    
    private static double _lastPlayerPosition = 0;
    private static DateTime _lastCheckTime = DateTime.Now;

    private static async Task Main()
    {
        while (true)
        {
            // check if Apple Music process is running
            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "pgrep",
                    Arguments = "^Music$",
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };
            try
            {
                process.Start();
            }
            catch (Exception e)
            {
                if (_client.IsInitialized) _client.Dispose();
                Console.WriteLine("Cant determine if Apple Music is running!");
                return;
            }
            await process.WaitForExitAsync();
            var appleMusicProcessId = process.StandardOutput.ReadToEnd().Trim();
            if (string.IsNullOrEmpty(appleMusicProcessId))
            {
                if (_client.IsInitialized) _client.Dispose();
                Console.WriteLine("Apple Music is not running! Next check in 5 seconds!");
                Thread.Sleep(5000);
                continue;
            }



            // check if Apple Music is playing
            var playerState = await ExecuteAppleScript(new[]
            {
                "tell application \\\"Music\\\"",
                "get player state",
                "end tell"
            });
            if (playerState.Trim() != "playing")
            {
                if (_client.IsInitialized && !_client.IsDisposed)
                {
                    if (_client.CurrentPresence)
                    {
                        _client.ClearPresence();
                        Console.WriteLine("discord presence cleared!");
                    }
                }
                _currentTrackId = "";
                Console.WriteLine("Apple Music is paused! Next check in 1 second!");
                Thread.Sleep(1000);
                continue;
            }

            
            // get current track info
            var nowPlaying = await ExecuteAppleScript(new[]
            {
                "set ouput to \\\"\\\"",
                "tell application \\\"Music\\\"",
                "set t_id to database id of current track",
                "set t_name to name of current track",
                "set t_album to album of current track",
                "set t_artist to artist of current track",
                "set t_duration to duration of current track",
                "set output to \\\"\\\" & t_id & \\\"\\n\\\" & t_name & \\\"\\n\\\" & t_album & \\\"\\n\\\" & t_artist & \\\"\\n\\\" & t_duration",
                "end tell",
                "return output"
            });
            var nowPlayingArray = nowPlaying.Split("\n");
            // check if the track has changed
            if (nowPlayingArray[0] != _currentTrackId)
            {
                _currentTrackId = nowPlayingArray[0];
            }
            else
            {
                // check if player position is as expected
                var playerPosition = double.Parse(await GetPlayerPosition(), CultureInfo.InvariantCulture);
                var expectedPlayerPosition = _lastPlayerPosition + (DateTime.Now - _lastCheckTime).TotalSeconds;
                if (Math.Abs(playerPosition - expectedPlayerPosition) > 3) 
                {
                    Console.WriteLine("Player position is not as expected! Updating Discord Rich Presence!");
                    await UpdateDiscordRichPresence(nowPlayingArray);
                }
                
                _lastPlayerPosition = playerPosition;
                _lastCheckTime = DateTime.Now;
                Thread.Sleep(1000);
                continue;
            }

            await UpdateDiscordRichPresence(nowPlayingArray);
            _lastPlayerPosition = double.Parse(await GetPlayerPosition(), CultureInfo.InvariantCulture);
            _lastCheckTime = DateTime.Now;
            Thread.Sleep(1000);
        }
    }

    private static async Task<string> GetPlayerPosition()
    {
        return await ExecuteAppleScript(new[]
        {
            "tell application \\\"Music\\\"",
            "set t_pos to player position",
            "return t_pos",
            "end tell"
        });
    }

    private static async Task<string> GetAlbumCoverUrl(string query)
    {
        var t1 = DateTime.Now;
        var res = await HttpClient.GetAsync(
            $"https://tools.applemediaservices.com/api/apple-media/music/US/search.json?types=songs&limit=1&term={query}");
        var json = JsonSerializer.Deserialize<AppleMusicSearchResult>(await res.Content.ReadAsStringAsync());
        Console.WriteLine($"Time taken to get album cover url: {DateTime.Now - t1}");
        return json.songs.data[0].attributes.artwork.url.Replace("{w}", "128").Replace("{h}", "128");
    }

    private static async Task UpdateDiscordRichPresence(string[] nowPlayingArray)
    {
        if (_client.IsDisposed) _client = new DiscordRpcClient(DiscordAppId);

        if (!_client.IsInitialized) _client.Initialize();

        _client.SkipIdenticalPresence = true;
        var playerPostion = await GetPlayerPosition();
        var url = await GetAlbumCoverUrl(nowPlayingArray[3] + " " + nowPlayingArray[1] + " " + nowPlayingArray[2]);
        playerPostion = playerPostion.Trim();
        _client.SetPresence(new RichPresence
        {
            State = nowPlayingArray[3],
            Details = nowPlayingArray[1],
            Assets = new Assets
            {
                LargeImageKey = url,
                LargeImageText = nowPlayingArray[1],
                SmallImageKey = "image_small"
            },
            Timestamps = new Timestamps
            {
                End = DateTime.Now.ToUniversalTime().AddSeconds(double.Parse(nowPlayingArray[4]) -
                                                                double.Parse(playerPostion,
                                                                    CultureInfo.InvariantCulture))
            }
        });
        Console.WriteLine($"Rich Presence set! {nowPlayingArray[1]} - {nowPlayingArray[3]} on {nowPlayingArray[2]}");
        Console.WriteLine(url);
    }


    private static async Task<string> ExecuteAppleScript(string[] args)
    {
        var formattedArgs = string.Join(" ", args.Select(a => $"-e '{a.Replace("'", "\\'")}'"));
        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "/bin/bash",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                Arguments = $"-c \"osascript {formattedArgs}"
            }
        };
        process.Start();
        var output = (await process.StandardOutput.ReadToEndAsync()).Trim();
        await process.WaitForExitAsync();
        return output;
    }

    public record AppleMusicSearchResult(
        Songs songs
    );

    public record Songs(
        string href,
        string next,
        Data[] data
    );

    public record Data(
        string id,
        string type,
        string href,
        Attributes attributes,
        Meta meta
    );

    public record Attributes(
        string albumName,
        bool hasTimeSyncedLyrics,
        string[] genreNames,
        int trackNumber,
        int durationInMillis,
        string releaseDate,
        bool isVocalAttenuationAllowed,
        bool isMasteredForItunes,
        string isrc,
        Artwork artwork,
        string audioLocale,
        string composerName,
        PlayParams playParams,
        string url,
        int discNumber,
        bool hasCredits,
        bool hasLyrics,
        bool isAppleDigitalMaster,
        string[] audioTraits,
        string name,
        Previews[] previews,
        string artistName
    );

    public record Artwork(
        int width,
        string url,
        int height,
        string textColor3,
        string textColor2,
        string textColor4,
        string textColor1,
        string bgColor,
        bool hasP3
    );

    public record PlayParams(
        string id,
        string kind
    );

    public record Previews(
        string url
    );

    public record Meta(
        ContentVersion contentVersion
    );

    public record ContentVersion(
        int MZ_INDEXER,
        int RTCI
    );
}