using System.Globalization;
using System.Text.Json;

namespace Neuroma.Speech;

public sealed record SpeechSettings(string KokoroUrl, string Voice, double Speed)
{
    public const string DefaultUrl = "http://127.0.0.1:8880";
    public const string DefaultVoice = "af_bella";

    public static SpeechSettings Load(string[] args, out HashSet<int> consumedArguments)
    {
        consumedArguments = [];
        SpeechConfig config = ReadConfig();
        string? requestedUrl = null;
        string? requestedVoice = null;
        double? requestedSpeed = null;

        for (int index = 0; index < args.Length; index++)
        {
            string argument = args[index];
            if (argument == "--kokoro-url")
            {
                requestedUrl = NextValue(args, ref index, consumedArguments, "--kokoro-url");
            }
            else if (argument.StartsWith("--kokoro-url=", StringComparison.Ordinal))
            {
                requestedUrl = argument["--kokoro-url=".Length..]; consumedArguments.Add(index);
            }
            else if (argument == "--voice")
            {
                requestedVoice = NextValue(args, ref index, consumedArguments, "--voice");
            }
            else if (argument.StartsWith("--voice=", StringComparison.Ordinal))
            {
                requestedVoice = argument["--voice=".Length..]; consumedArguments.Add(index);
            }
            else if (argument == "--speed")
            {
                requestedSpeed = ParseSpeed(NextValue(args, ref index, consumedArguments, "--speed"));
            }
            else if (argument.StartsWith("--speed=", StringComparison.Ordinal))
            {
                requestedSpeed = ParseSpeed(argument["--speed=".Length..]); consumedArguments.Add(index);
            }
        }

        string url = (requestedUrl ?? config.KokoroUrl ?? DefaultUrl).TrimEnd('/');
        string voice = requestedVoice ?? config.Voice ?? DefaultVoice;
        double speed = requestedSpeed ?? config.Speed ?? 1;
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) || uri.Scheme is not ("http" or "https"))
            throw new SpeechConfigurationException("Kokoro URL must be an absolute HTTP or HTTPS URL.");
        if (string.IsNullOrWhiteSpace(voice)) throw new SpeechConfigurationException("Kokoro voice cannot be empty.");
        if (!double.IsFinite(speed) || speed is < 0.25 or > 4)
            throw new SpeechConfigurationException("Kokoro speed must be between 0.25 and 4.");
        return new SpeechSettings(url, voice, speed);
    }

    private static SpeechConfig ReadConfig()
    {
        string besideExecutable = Path.Combine(AppContext.BaseDirectory, "neuroma.json");
        string currentDirectory = Path.Combine(Directory.GetCurrentDirectory(), "neuroma.json");
        string? path = File.Exists(besideExecutable) ? besideExecutable : File.Exists(currentDirectory) ? currentDirectory : null;
        if (path is null) return new SpeechConfig();
        try
        {
            return JsonSerializer.Deserialize<SpeechConfig>(File.ReadAllText(path), new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            }) ?? new SpeechConfig();
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            throw new SpeechConfigurationException($"Could not read speech configuration at '{path}'.", ex);
        }
    }

    private static string NextValue(string[] args, ref int index, HashSet<int> consumed, string option)
    {
        consumed.Add(index);
        if (index + 1 >= args.Length) throw new SpeechConfigurationException($"{option} requires a value.");
        consumed.Add(index + 1);
        return args[++index];
    }

    private static double ParseSpeed(string value)
    {
        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double speed))
            throw new SpeechConfigurationException("Kokoro speed must be a number.");
        return speed;
    }

    private sealed class SpeechConfig
    {
        public string? KokoroUrl { get; set; }
        public string? Voice { get; set; }
        public double? Speed { get; set; }
    }
}

public sealed class SpeechConfigurationException : Exception
{
    public SpeechConfigurationException(string message) : base(message) { }
    public SpeechConfigurationException(string message, Exception innerException) : base(message, innerException) { }
}
