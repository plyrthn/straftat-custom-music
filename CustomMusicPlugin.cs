using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using System.Linq;
using System.Reflection;

[BepInPlugin("com.plyrthn.straftat.custommusic", "STRAFTAT Custom Music", "1.0.0")]
public class CustomMusicPlugin : BaseUnityPlugin
{
    internal new static ManualLogSource Logger = null!;
    private static string CustomMusicFolder = null!;
    private static readonly List<CustomMusicInfo> PendingMusicFiles = new List<CustomMusicInfo>();

    // config stuff
    private static ConfigEntry<bool> CustomOnlyMode = null!;
    private static bool CustomOnlyModeValue = false; // backup if config fails

    void Awake()
    {
        Logger = base.Logger;
        CustomMusicFolder = Path.Combine(Paths.ConfigPath, "CustomMusic");

        if (!Directory.Exists(CustomMusicFolder))
        {
            Directory.CreateDirectory(CustomMusicFolder);
            CreateSampleFiles();
            Logger.LogInfo($"Created custom music folder at: {CustomMusicFolder}");
        }

        // config setup
        Logger.LogInfo("Setting up config...");
        try
        {
            CustomOnlyMode = Config.Bind("General", "CustomOnlyMode", false,
                "Set to true to replace all original music with only custom tracks. Set to false to mix custom tracks with original soundtrack.");
            CustomOnlyModeValue = CustomOnlyMode.Value;
            Logger.LogInfo($"Config loaded: CustomOnlyMode = {CustomOnlyModeValue}");
        }
        catch (System.Exception ex)
        {
            Logger.LogError($"Config failed: {ex.Message}");
            Logger.LogInfo("Making config file manually...");
            CreateManualConfigFile();
        }

        LoadCustomMusicList();

        try
        {
            var harmony = new Harmony("com.plyrthn.straftat.custommusic");
            harmony.PatchAll();
            Logger.LogInfo("Harmony patches applied.");

            var patches = harmony.GetPatchedMethods().ToList();
            Logger.LogInfo($"Got {patches.Count} patched methods.");

            if (patches.Count == 0)
            {
                Logger.LogWarning("No patches worked! Checking what went wrong...");
                InvestigateTargetMethods();
            }
        }
        catch (System.Exception ex)
        {
            Logger.LogError($"Harmony failed: {ex.Message}");
            Logger.LogError($"Stack trace: {ex.StackTrace}");
        }

        Logger.LogInfo("Custom Music Plugin loaded!");

        // backup if harmony doesn't work
        StartCoroutine(MonitorForMusicManager());
    }

    private void CreateManualConfigFile()
    {
        try
        {
            var configDir = Path.Combine(Paths.ConfigPath);
            if (!Directory.Exists(configDir))
            {
                Directory.CreateDirectory(configDir);
                Logger.LogInfo($"Made config directory: {configDir}");
            }

            var configFilePath = Path.Combine(configDir, "com.plyrthn.straftat.custommusic.cfg");
            if (!File.Exists(configFilePath))
            {
                var configContent = @"## Settings file was created by plugin STRAFTAT Custom Music v1.0.0
## Plugin GUID: com.plyrthn.straftat.custommusic

[General]

## Set to true to replace all original music with only custom tracks. Set to false to mix custom tracks with original soundtrack.
# Setting type: Boolean
# Default value: false
CustomOnlyMode = false
";
                File.WriteAllText(configFilePath, configContent);
                Logger.LogInfo($"Created config file at: {configFilePath}");
                Logger.LogInfo("Edit this file to change CustomOnlyMode setting.");
            }
            else
            {
                Logger.LogInfo($"Config file already exists: {configFilePath}");
            }
        }
        catch (System.Exception ex)
        {
            Logger.LogError($"Couldn't create config file: {ex.Message}");
        }
    }

    static void RefreshMusicUI(SetMenuMusicVolume musicManager)
    {
        try
        {
            var currentTrack = musicManager.MusicTracks[musicManager.currentTrackId];

            // update UI text with reflection hacks
            try
            {
                var trackTextType = typeof(SetMenuMusicVolume);
                var trackTextInPlayerField = trackTextType.GetField("trackTextInPlayer", BindingFlags.NonPublic | BindingFlags.Instance);
                var trackTextField = trackTextType.GetField("trackText", BindingFlags.NonPublic | BindingFlags.Instance);

                var displayText = currentTrack.AudioClip.name + " - " + currentTrack.ArtistName;

                if (trackTextInPlayerField != null)
                {
                    var trackTextInPlayer = trackTextInPlayerField.GetValue(musicManager);
                    if (trackTextInPlayer != null)
                    {
                        var textProperty = trackTextInPlayer.GetType().GetProperty("text");
                        textProperty?.SetValue(trackTextInPlayer, displayText);
                    }
                }

                if (trackTextField != null)
                {
                    var trackText = trackTextField.GetValue(musicManager);
                    if (trackText != null)
                    {
                        var textProperty = trackText.GetType().GetProperty("text");
                        textProperty?.SetValue(trackText, "music playing : " + displayText);
                    }
                }
            }
            catch (System.Exception ex)
            {
                Logger.LogWarning($"Couldn't update UI text: {ex.Message}");
            }

            // fix button states
            try
            {
                var pauseButtonField = typeof(SetMenuMusicVolume).GetField("pauseButton", BindingFlags.NonPublic | BindingFlags.Instance);
                var playButtonField = typeof(SetMenuMusicVolume).GetField("playButton", BindingFlags.NonPublic | BindingFlags.Instance);

                if (pauseButtonField != null && playButtonField != null)
                {
                    var pauseButton = pauseButtonField.GetValue(musicManager) as GameObject;
                    var playButton = playButtonField.GetValue(musicManager) as GameObject;

                    if (pauseButton != null && playButton != null)
                    {
                        pauseButton.SetActive(musicManager.audio.isPlaying);
                        playButton.SetActive(!musicManager.audio.isPlaying);
                    }
                }
            }
            catch (System.Exception ex)
            {
                Logger.LogWarning($"Couldn't update buttons: {ex.Message}");
            }
        }
        catch (System.Exception ex)
        {
            Logger.LogError($"Error checking track: {ex.Message}");
        }
    }

    private IEnumerator MonitorForMusicManager()
    {
        Logger.LogInfo("Looking for music manager...");

        while (true)
        {
            yield return new WaitForSeconds(1f);

            var musicManager = UnityEngine.Object.FindObjectOfType<SetMenuMusicVolume>();
            if (musicManager != null)
            {
                Logger.LogInfo("Found SetMenuMusicVolume!");

                if (PendingMusicFiles.Count > 0)
                {
                    Logger.LogInfo("Loading music directly...");
                    StartCoroutine(LoadCustomMusicCoroutine(musicManager));
                }
                break;
            }
        }
    }

    private void DiagnoseAvailableTypes()
    {
        try
        {
            Logger.LogInfo("=== Looking for SetMenuMusicVolume class ===");

            var assemblies = System.AppDomain.CurrentDomain.GetAssemblies();
            foreach (var assembly in assemblies)
            {
                try
                {
                    var types = assembly.GetTypes().Where(t =>
                        t.Name.Contains("Music") ||
                        t.Name.Contains("Volume") ||
                        t.Name.Contains("SetMenu")
                    ).ToArray();

                    if (types.Length > 0)
                    {
                        Logger.LogInfo($"Assembly {assembly.GetName().Name} has music stuff:");
                        foreach (var type in types)
                        {
                            Logger.LogInfo($"  - {type.FullName}");
                        }
                    }
                }
                catch (System.Exception ex)
                {
                    Logger.LogWarning($"Couldn't check assembly {assembly.GetName().Name}: {ex.Message}");
                }
            }
        }
        catch (System.Exception ex)
        {
            Logger.LogError($"Error diagnosing: {ex.Message}");
        }
    }

    private void InvestigateTargetMethods()
    {
        try
        {
            Logger.LogInfo("=== Checking target methods ===");

            var targetType = System.Type.GetType("SetMenuMusicVolume");
            if (targetType == null)
            {
                // search all assemblies
                var assemblies = System.AppDomain.CurrentDomain.GetAssemblies();
                foreach (var assembly in assemblies)
                {
                    try
                    {
                        targetType = assembly.GetType("SetMenuMusicVolume");
                        if (targetType != null)
                        {
                            Logger.LogInfo($"Found SetMenuMusicVolume in: {assembly.GetName().Name}");
                            break;
                        }
                    }
                    catch { }
                }
            }

            if (targetType != null)
            {
                Logger.LogInfo($"Found type: {targetType.FullName}");

                var methods = targetType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static);
                Logger.LogInfo($"Methods in {targetType.Name}:");
                foreach (var method in methods)
                {
                    var paramStr = string.Join(", ", method.GetParameters().Select(p => $"{p.ParameterType.Name} {p.Name}"));
                    Logger.LogInfo($"  - {method.ReturnType.Name} {method.Name}({paramStr})");
                }
            }
            else
            {
                Logger.LogError("Can't find SetMenuMusicVolume anywhere!");
            }
        }
        catch (System.Exception ex)
        {
            Logger.LogError($"Error checking methods: {ex.Message}");
        }
    }

    private void CreateSampleFiles()
    {
        var readmeContent = @"STRAFTAT Custom Music Folder

Add your MP3 files here and they'll be loaded into the game!

Supported Formats: MP3, OGG, WAV

Metadata Priority:
1. ID3 Tags (recommended) - Use any music tagger like Mp3tag
2. JSON files - Create ""SongName.json"" with: {""artist"": ""Name"", ""title"": ""Song""}
3. Text files - Create ""SongName.txt"" with just the artist name
4. Filename format - ""Song Title - Artist Name.mp3""
5. Folder structure - ""Artist Name/Song Title.mp3""

Configuration:
Edit the config file at BepInEx/config/com.plyrthn.straftat.custommusic.cfg
Set CustomOnlyMode = true to replace all original music with only your custom tracks
Set CustomOnlyMode = false to mix your custom tracks with the original soundtrack

Examples:
  CustomMusic/
  ├── Awesome Song - Cool Artist.mp3
  ├── My Track.mp3
  ├── My Track.json
  └── Daft Punk/
      └── One More Time.mp3

The mod will automatically detect new files when you restart the game.
";
        File.WriteAllText(Path.Combine(CustomMusicFolder, "README.txt"), readmeContent);
    }

    private static void LoadCustomMusicList()
    {
        PendingMusicFiles.Clear();

        if (!Directory.Exists(CustomMusicFolder))
            return;

        var supportedExtensions = new[] { "*.mp3", "*.ogg", "*.wav" };
        var musicFiles = supportedExtensions
            .SelectMany(ext => Directory.GetFiles(CustomMusicFolder, ext, SearchOption.AllDirectories))
            .ToArray();

        foreach (var musicFile in musicFiles)
        {
            try
            {
                var fileName = Path.GetFileNameWithoutExtension(musicFile);
                var artistName = GetBestMetadata(musicFile);
                var songTitle = fileName;

                // handle "Song - Artist" filename format
                if (fileName.Contains(" - "))
                {
                    var parts = fileName.Split(new[] { " - " }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 2)
                    {
                        songTitle = parts[0]; // song is first part
                        var fileBasedArtist = string.Join(" - ", parts.Skip(1)); // artist is everything after first " - "

                        // use filename artist if metadata didn't work
                        if (artistName == "Unknown Artist" || artistName == fileBasedArtist)
                        {
                            artistName = fileBasedArtist;
                        }
                    }
                }

                var musicInfo = new CustomMusicInfo
                {
                    FilePath = musicFile,
                    FileName = songTitle, // becomes clip.name
                    ArtistName = artistName
                };

                PendingMusicFiles.Add(musicInfo);
                Logger.LogInfo($"Found: {musicInfo.FileName} - {musicInfo.ArtistName}");
            }
            catch (System.Exception ex)
            {
                Logger.LogError($"Error processing {musicFile}: {ex.Message}");
            }
        }

        Logger.LogInfo($"Found {PendingMusicFiles.Count} custom music files");
    }

    // patch Awake to extend arrays before MusicTracks gets created
    [HarmonyPatch]
    static class AwakePatch
    {
        static MethodBase TargetMethod()
        {
            return typeof(SetMenuMusicVolume).GetMethod("Awake", BindingFlags.NonPublic | BindingFlags.Instance);
        }

        static void Prefix(SetMenuMusicVolume __instance)
        {
            if (PendingMusicFiles.Count == 0)
                return;

            Logger.LogInfo($"Extending arrays for {PendingMusicFiles.Count} custom tracks...");
            ExtendMusicArraysSync(__instance);
        }

        static void Postfix(SetMenuMusicVolume __instance)
        {
            // stop audio to prevent playing dummy clips
            if (PendingMusicFiles.Count > 0 && __instance.audio != null)
            {
                __instance.audio.Stop();
            }
        }
    }

    // load actual audio files
    [HarmonyPatch]
    static class StartPatch
    {
        static MethodBase TargetMethod()
        {
            return typeof(SetMenuMusicVolume).GetMethod("Start", BindingFlags.NonPublic | BindingFlags.Instance);
        }

        static void Prefix(SetMenuMusicVolume __instance)
        {
            if (PendingMusicFiles.Count == 0)
                return;

            Logger.LogInfo($"Loading {PendingMusicFiles.Count} custom music files...");
            __instance.StartCoroutine(LoadCustomMusicCoroutine(__instance));
        }
    }

    [HarmonyPatch]
    static class UpdatePatch
    {
        static MethodBase TargetMethod()
        {
            return typeof(SetMenuMusicVolume).GetMethod("Update", BindingFlags.NonPublic | BindingFlags.Instance);
        }

        static void Prefix()
        {
            // just here to verify patches work
        }
    }

    static void ExtendMusicArraysSync(SetMenuMusicVolume musicManager)
    {
        try
        {
            // use backup value if config failed
            bool useCustomOnlyMode = CustomOnlyMode?.Value ?? CustomOnlyModeValue;

            if (useCustomOnlyMode)
            {
                Logger.LogInfo($"Custom-only mode: Replacing {musicManager.audioClips.Length} original tracks with {PendingMusicFiles.Count} custom");

                // replace everything
                var customAudioClips = new List<AudioClip>();
                var customTrackNames = new List<string>();

                foreach (var musicFile in PendingMusicFiles)
                {
                    var dummyClip = AudioClip.Create(musicFile.FileName, 44100, 1, 44100, false);
                    customAudioClips.Add(dummyClip);
                    customTrackNames.Add(musicFile.ArtistName);
                }

                musicManager.audioClips = customAudioClips.ToArray();
                musicManager.trackNames = customTrackNames.ToArray();

                Logger.LogInfo($"Custom-only mode: {musicManager.audioClips.Length} tracks");
            }
            else
            {
                Logger.LogInfo($"Mixed mode: Extending {musicManager.audioClips.Length} original + {PendingMusicFiles.Count} custom");

                var originalClipCount = musicManager.audioClips.Length;
                var extendedAudioClips = new List<AudioClip>(musicManager.audioClips);
                var extendedTrackNames = new List<string>(musicManager.trackNames);

                // add dummy clips so MusicTracks array gets the right size
                foreach (var musicFile in PendingMusicFiles)
                {
                    var dummyClip = AudioClip.Create(musicFile.FileName, 44100, 1, 44100, false);
                    extendedAudioClips.Add(dummyClip);
                    extendedTrackNames.Add(musicFile.ArtistName);
                }

                musicManager.audioClips = extendedAudioClips.ToArray();
                musicManager.trackNames = extendedTrackNames.ToArray();

                Logger.LogInfo($"Mixed mode: {originalClipCount} original + {PendingMusicFiles.Count} custom = {musicManager.audioClips.Length} total");
            }
        }
        catch (System.Exception ex)
        {
            Logger.LogError($"Error extending arrays: {ex.Message}");
        }
    }

    static IEnumerator LoadCustomMusicCoroutine(SetMenuMusicVolume musicManager)
    {
        // use backup value if config failed
        bool useCustomOnlyMode = CustomOnlyMode?.Value ?? CustomOnlyModeValue;
        int startIndex = useCustomOnlyMode ? 0 : musicManager.audioClips.Length - PendingMusicFiles.Count;

        int successCount = 0;
        for (int i = 0; i < PendingMusicFiles.Count; i++)
        {
            var musicFile = PendingMusicFiles[i];
            var targetIndex = startIndex + i;

            yield return LoadAudioFileCoroutineToIndex(musicFile, musicManager, targetIndex);
            successCount++;
        }

        if (successCount > 0)
        {
            RecreateMusimcTracks(musicManager);

            // reshuffle so custom tracks get mixed in
            musicManager.Shuffle();

            // safe to start playing again
            if (!musicManager.audio.isPlaying)
            {
                // make sure we're pointing to valid track
                musicManager.currentTrackId = Mathf.Clamp(musicManager.currentTrackId, 0, musicManager.MusicTracks.Length - 1);

                var currentTrack = musicManager.MusicTracks[musicManager.currentTrackId];
                musicManager.audio.clip = currentTrack.AudioClip;
                musicManager.audio.Play();

                // force UI update
                RefreshMusicUI(musicManager);
            }

            var modeText = useCustomOnlyMode ? "custom-only" : "mixed";
            Logger.LogInfo($"Loaded {successCount}/{PendingMusicFiles.Count} custom tracks in {modeText} mode");
        }
        else
        {
            Logger.LogWarning("No custom music files loaded successfully");
        }
    }

    static IEnumerator LoadAudioFileCoroutineToIndex(CustomMusicInfo musicInfo, SetMenuMusicVolume musicManager, int targetIndex)
    {
        var filePath = musicInfo.FilePath;

        if (!File.Exists(filePath))
        {
            Logger.LogError($"File not found: {filePath}");
            yield break;
        }

        var fileUri = new System.Uri(filePath).AbsoluteUri;

        var extension = Path.GetExtension(musicInfo.FilePath).ToLowerInvariant();
        var audioType = extension switch
        {
            ".mp3" => AudioType.MPEG,
            ".ogg" => AudioType.OGGVORBIS,
            ".wav" => AudioType.WAV,
            _ => AudioType.MPEG
        };

        using (var www = UnityEngine.Networking.UnityWebRequestMultimedia.GetAudioClip(fileUri, audioType))
        {
            yield return www.SendWebRequest();

            if (www.result == UnityEngine.Networking.UnityWebRequest.Result.Success)
            {
                try
                {
                    var audioClip = UnityEngine.Networking.DownloadHandlerAudioClip.GetContent(www);
                    if (audioClip != null && audioClip.length > 0)
                    {
                        audioClip.name = musicInfo.FileName;
                        musicManager.audioClips[targetIndex] = audioClip;
                        Logger.LogInfo($"Loaded: {musicInfo.FileName} - {musicInfo.ArtistName}");
                    }
                    else
                    {
                        Logger.LogError($"Failed to load {musicInfo.FileName}: AudioClip is null or zero length");
                    }
                }
                catch (System.Exception ex)
                {
                    Logger.LogError($"Exception loading {musicInfo.FileName}: {ex.Message}");
                }
            }
            else
            {
                Logger.LogError($"Failed to load {musicInfo.FilePath}: {www.error}");
            }
        }
    }

    private static void RecreateMusimcTracks(SetMenuMusicVolume musicManager)
    {
        try
        {
            var musicTrackType = typeof(MusicTrack);
            var constructors = musicTrackType.GetConstructors();

            if (constructors.Length == 0)
            {
                Logger.LogError("No constructors found for MusicTrack");
                return;
            }

            var musicTracks = new MusicTrack[musicManager.audioClips.Length];

            for (int i = 0; i < musicManager.audioClips.Length; i++)
            {
                try
                {
                    var musicTrack = (MusicTrack)System.Activator.CreateInstance(
                        musicTrackType,
                        musicManager.audioClips[i],
                        musicManager.trackNames[i],
                        i
                    );

                    musicTracks[i] = musicTrack;
                }
                catch (System.Exception ex)
                {
                    Logger.LogError($"Error creating MusicTrack at {i}: {ex.Message}");
                    Logger.LogError($"Params: AudioClip={musicManager.audioClips[i]?.name}, TrackName={musicManager.trackNames[i]}, Index={i}");
                    return;
                }
            }

            musicManager.MusicTracks = musicTracks;
        }
        catch (System.Exception ex)
        {
            Logger.LogError($"Error recreating MusicTracks: {ex.Message}");
        }
    }

    static string GetBestMetadata(string filePath)
    {
        var fileName = Path.GetFileNameWithoutExtension(filePath);
        var directory = Path.GetDirectoryName(filePath) ?? "";
        var folderName = Path.GetFileName(directory);

        // try ID3 tags first
        var id3Tags = ReadMP3Tags(filePath);
        if (!string.IsNullOrEmpty(id3Tags))
            return id3Tags;

        // check for .json metadata
        var jsonFile = Path.ChangeExtension(filePath, ".json");
        if (File.Exists(jsonFile))
        {
            var metadata = ParseJsonMetadata(File.ReadAllText(jsonFile));
            if (!string.IsNullOrEmpty(metadata))
                return metadata;
        }

        // check for .txt with artist name
        var txtFile = Path.ChangeExtension(filePath, ".txt");
        if (File.Exists(txtFile))
        {
            var textContent = File.ReadAllText(txtFile).Trim();
            if (!string.IsNullOrEmpty(textContent))
                return textContent;
        }

        // parse "Song Title - Artist Name.mp3"
        var parts = fileName.Split(new[] { " - " }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 2)
        {
            // get artist part to avoid duplication
            var artistPart = string.Join(" - ", parts.Skip(1));
            return artistPart;
        }

        // use folder name if not root
        if (folderName != "CustomMusic" && !string.IsNullOrEmpty(folderName))
            return folderName;

        return "Unknown Artist";
    }

    static string? ReadMP3Tags(string filePath)
    {
        if (!filePath.EndsWith(".mp3", System.StringComparison.OrdinalIgnoreCase))
            return null;

        try
        {
            using var fileStream = new FileStream(filePath, FileMode.Open, FileAccess.Read);

            // try ID3v2 first (beginning)
            var id3v2 = ReadID3v2Tags(fileStream);
            if (!string.IsNullOrEmpty(id3v2))
                return id3v2;

            // fallback to ID3v1 (end)
            var id3v1 = ReadID3v1Tags(fileStream);
            if (!string.IsNullOrEmpty(id3v1))
                return id3v1;
        }
        catch (System.Exception ex)
        {
            Logger.LogWarning($"Failed to read ID3 tags from {Path.GetFileName(filePath)}: {ex.Message}");
        }

        return null;
    }

    static string? ReadID3v2Tags(FileStream stream)
    {
        stream.Seek(0, SeekOrigin.Begin);
        var header = new byte[10];
        if (stream.Read(header, 0, 10) != 10)
            return null;

        // check ID3v2 header
        if (header[0] != 'I' || header[1] != 'D' || header[2] != '3')
            return null;

        // parse size (syncsafe integer)
        var size = (header[6] << 21) | (header[7] << 14) | (header[8] << 7) | header[9];
        if (size <= 0 || size > stream.Length - 10)
            return null;

        var tagData = new byte[size];
        if (stream.Read(tagData, 0, size) != size)
            return null;

        // look for artist first
        var artist = ExtractID3v2Frame(tagData, "TPE1") ?? ExtractID3v2Frame(tagData, "TPE2");
        if (!string.IsNullOrEmpty(artist))
            return artist;

        // fallback to title
        var title = ExtractID3v2Frame(tagData, "TIT2");
        if (!string.IsNullOrEmpty(title))
            return title;

        return null;
    }

    static string? ExtractID3v2Frame(byte[] data, string frameId)
    {
        var frameBytes = System.Text.Encoding.ASCII.GetBytes(frameId);

        // scan for frame
        for (int i = 0; i <= data.Length - 10; i++)
        {
            if (data[i] == frameBytes[0] && data[i + 1] == frameBytes[1] &&
                data[i + 2] == frameBytes[2] && data[i + 3] == frameBytes[3])
            {
                var frameSize = (data[i + 4] << 24) | (data[i + 5] << 16) | (data[i + 6] << 8) | data[i + 7];

                if (frameSize > 0 && frameSize < 1000000 && i + 10 + frameSize <= data.Length)
                {
                    var encoding = data[i + 10];
                    var textStart = i + 11;
                    var textLength = frameSize - 1;

                    if (textLength > 0)
                    {
                        var textData = new byte[textLength];
                        System.Array.Copy(data, textStart, textData, 0, textLength);

                        // handle different encodings
                        var result = encoding switch
                        {
                            0 => System.Text.Encoding.ASCII.GetString(textData),
                            1 => System.Text.Encoding.Unicode.GetString(textData),
                            3 => System.Text.Encoding.UTF8.GetString(textData),
                            _ => System.Text.Encoding.UTF8.GetString(textData)
                        };

                        return result.Trim('\0', ' ');
                    }
                }
            }
        }
        return null;
    }

    static string? ReadID3v1Tags(FileStream stream)
    {
        if (stream.Length < 128)
            return null;

        // ID3v1 is always last 128 bytes
        stream.Seek(-128, SeekOrigin.End);
        var tag = new byte[128];
        if (stream.Read(tag, 0, 128) != 128)
            return null;

        // check TAG header
        if (tag[0] != 'T' || tag[1] != 'A' || tag[2] != 'G')
            return null;

        // artist at offset 33, 30 bytes
        var artist = System.Text.Encoding.ASCII.GetString(tag, 33, 30).Trim('\0', ' ');
        if (!string.IsNullOrEmpty(artist))
            return artist;

        // fallback to title at offset 3, 30 bytes
        var title = System.Text.Encoding.ASCII.GetString(tag, 3, 30).Trim('\0', ' ');
        if (!string.IsNullOrEmpty(title))
            return title;

        return null;
    }

    static string? ParseJsonMetadata(string jsonContent)
    {
        try
        {
            // simple regex instead of full JSON library
            var artistMatch = System.Text.RegularExpressions.Regex.Match(jsonContent, @"""artist""\s*:\s*""([^""]+)""");
            if (artistMatch.Success)
                return artistMatch.Groups[1].Value;

            var titleMatch = System.Text.RegularExpressions.Regex.Match(jsonContent, @"""title""\s*:\s*""([^""]+)""");
            if (titleMatch.Success)
                return titleMatch.Groups[1].Value;
        }
        catch (System.Exception ex)
        {
            Logger.LogWarning($"Error parsing JSON: {ex.Message}");
        }
        return null;
    }
}

public class CustomMusicInfo
{
    public string FilePath = "";
    public string FileName = "";
    public string ArtistName = "";
}