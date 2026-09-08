using System.Buffers.Binary;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Neuroma.Speech;

public sealed partial class KokoroNarrator : IAsyncDisposable
{
    private readonly SpeechSettings _settings;
    private readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromMinutes(2) };
    private readonly object _gate = new();
    private CancellationTokenSource? _cancellation;
    private Task? _task;
    private bool? _captionApiAvailable;
    private bool _isRunning;
    private bool _isPaused;
    private string _status = "KOKORO · READY";
    private string _resumeStatus = "KOKORO · READY";
    private SpeechCue? _currentCue;
    private TaskCompletionSource<bool>? _resumeSignal;
    private MciWavePlayer? _activePlayer;
    private string _kokoroUrl;

    public KokoroNarrator(SpeechSettings settings)
    {
        _settings = settings;
        _kokoroUrl = settings.KokoroUrl;
    }
    public event Action? Changed;

    public bool IsRunning { get { lock (_gate) return _isRunning; } }
    public bool IsPaused { get { lock (_gate) return _isPaused; } }
    public string Status { get { lock (_gate) return _status; } }
    public SpeechCue? CurrentCue { get { lock (_gate) return _currentCue; } }
    public string KokoroUrl { get { lock (_gate) return _kokoroUrl; } }

    public async Task<bool> IsEndpointReachableAsync(int timeoutMilliseconds = 3000)
    {
        string endpoint = KokoroUrl;
        using var timeout = new CancellationTokenSource(Math.Max(1, timeoutMilliseconds));
        try
        {
            using HttpResponseMessage response = await _httpClient.GetAsync(
                $"{endpoint}/openapi.json", HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
        {
            return false;
        }
    }

    public void UseLocalEndpoint()
    {
        lock (_gate)
        {
            _kokoroUrl = SpeechSettings.DefaultUrl;
            _captionApiAvailable = null;
        }
    }

    internal void ReportStatus(string status) => SetStatus(status);

    public void Start(IReadOnlyList<SpeechChunk> chunks)
    {
        if (chunks.Count == 0)
        {
            SetStatus("KOKORO FAILED · no readable text after this line");
            return;
        }

        CancellationTokenSource cancellation;
        Task task;
        lock (_gate)
        {
            if (_isRunning) return;
            cancellation = new CancellationTokenSource();
            _cancellation = cancellation;
            _isRunning = true;
            _isPaused = false;
            _resumeSignal = null;
            _status = $"KOKORO · STARTING · {_settings.Voice}";
            task = RunAsync(chunks, cancellation.Token);
            _task = task;
        }
        Changed?.Invoke();
        _ = ObserveAsync(task, cancellation);
    }

    public void Pause()
    {
        MciWavePlayer? player;
        lock (_gate)
        {
            if (!_isRunning || _isPaused) return;
            _isPaused = true;
            _resumeSignal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _resumeStatus = _status;
            _status = "KOKORO · PAUSED · s TO RESUME";
            player = _activePlayer;
        }
        player?.TryPause();
        Changed?.Invoke();
    }

    public void Resume()
    {
        MciWavePlayer? player;
        TaskCompletionSource<bool>? signal;
        lock (_gate)
        {
            if (!_isRunning || !_isPaused) return;
            _isPaused = false;
            _status = _resumeStatus;
            player = _activePlayer;
            signal = _resumeSignal;
            _resumeSignal = null;
        }
        player?.TryResume();
        signal?.TrySetResult(true);
        Changed?.Invoke();
    }

    public void Stop(string message = "KOKORO · STOPPED")
    {
        CancellationTokenSource? cancellation;
        TaskCompletionSource<bool>? signal;
        MciWavePlayer? player;
        lock (_gate)
        {
            cancellation = _cancellation;
            signal = _resumeSignal;
            player = _activePlayer;
            _isPaused = false;
            _resumeSignal = null;
            _status = message;
            _currentCue = null;
        }
        signal?.TrySetResult(true);
        player?.TryStop();
        cancellation?.Cancel();
        Changed?.Invoke();
    }

    public async ValueTask DisposeAsync()
    {
        Stop();
        Task? task;
        lock (_gate) task = _task;
        if (task is not null)
        {
            try { await task.ConfigureAwait(false); } catch (OperationCanceledException) { }
        }
        _httpClient.Dispose();
        _cancellation?.Dispose();
    }

    private async Task ObserveAsync(Task task, CancellationTokenSource owner)
    {
        string? failure = null;
        try { await task.ConfigureAwait(false); }
        catch (OperationCanceledException) when (owner.IsCancellationRequested) { }
        catch (Exception ex) { failure = ex.Message; }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_cancellation, owner))
                {
                    _isRunning = false;
                    _task = null;
                    _cancellation = null;
                    _isPaused = false;
                    _resumeSignal = null;
                    _activePlayer = null;
                    _currentCue = null;
                    if (failure is not null) _status = $"KOKORO FAILED · {failure}";
                    else if (!owner.IsCancellationRequested) _status = "KOKORO · COMPLETE";
                }
            }
            owner.Dispose();
            Changed?.Invoke();
        }
    }

    private async Task RunAsync(IReadOnlyList<SpeechChunk> chunks, CancellationToken cancellationToken)
    {
        SetStatus($"KOKORO · GENERATING 1/{chunks.Count} · {_settings.Voice}");
        await SpeechPrefetchPipeline.RunAsync(
            chunks,
            (chunk, _, token) => RequestSpeechAsync(chunk, token),
            async (chunk, speech, index, hasNext, token) =>
            {
                IReadOnlyList<SpeechCue> cues = BuildCues(chunk, speech.Timestamps);
                string prefetch = hasNext ? $" · PREFETCHING {index + 2}/{chunks.Count}" : "";
                await DelayRespectingPauseAsync(chunk.PauseBeforeMilliseconds, token).ConfigureAwait(false);
                SetStatus($"KOKORO · PLAYING {index + 1}/{chunks.Count} · " +
                    $"{(speech.Exact ? "EXACT" : "ESTIMATED")} TIMING{prefetch} · s TO PAUSE");
                await PlaySpeechAsync(speech.Audio, cues, token).ConfigureAwait(false);
                await DelayRespectingPauseAsync(chunk.PauseAfterMilliseconds, token).ConfigureAwait(false);
            },
            index => SetStatus($"KOKORO · WAITING FOR BUFFER {index + 1}/{chunks.Count}"),
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<SpeechResult> RequestSpeechAsync(SpeechChunk chunk, CancellationToken cancellationToken)
    {
        string kokoroUrl = KokoroUrl;
        var body = new
        {
            model = "kokoro",
            input = chunk.Text,
            voice = _settings.Voice,
            response_format = "wav",
            speed = _settings.Speed,
            stream = false
        };

        if (_captionApiAvailable != false)
        {
            using HttpResponseMessage captionResponse = await PostJsonAsync(
                $"{kokoroUrl}/dev/captioned_speech",
                new
                {
                    body.model, body.input, body.voice, body.response_format, body.speed, body.stream,
                    return_timestamps = true
                }, cancellationToken).ConfigureAwait(false);
            if (captionResponse.IsSuccessStatusCode)
            {
                byte[] json = await captionResponse.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
                _captionApiAvailable = true;
                return ParseCaptionedSpeech(json);
            }
            if (captionResponse.StatusCode != HttpStatusCode.NotFound)
                throw new InvalidOperationException($"captioned speech returned HTTP {(int)captionResponse.StatusCode}");
            _captionApiAvailable = false;
        }

        using HttpResponseMessage response = await PostJsonAsync(
            $"{kokoroUrl}/v1/audio/speech", body, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Kokoro returned HTTP {(int)response.StatusCode}");
        byte[] audio = RepairWavHeader(await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false));
        return new SpeechResult(audio, EstimateTimestamps(chunk.Text, WavDuration(audio)), Exact: false);
    }

    private async Task<HttpResponseMessage> PostJsonAsync(string url, object body, CancellationToken cancellationToken)
    {
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(body);
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new ByteArrayContent(json)
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        return await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
    }

    private static SpeechResult ParseCaptionedSpeech(byte[] json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;
        string? encodedAudio = root.TryGetProperty("audio", out JsonElement audioElement) ? audioElement.GetString() : null;
        if (string.IsNullOrWhiteSpace(encodedAudio)) throw new InvalidDataException("captioned speech returned no audio");
        var timestamps = new List<SpeechTimestamp>();
        if (root.TryGetProperty("timestamps", out JsonElement values) && values.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement value in values.EnumerateArray())
            {
                string word = value.TryGetProperty("word", out JsonElement wordElement) ? wordElement.GetString() ?? "" : "";
                double start = value.TryGetProperty("start_time", out JsonElement startElement) ? startElement.GetDouble() : 0;
                double end = value.TryGetProperty("end_time", out JsonElement endElement) ? endElement.GetDouble() : start;
                timestamps.Add(new SpeechTimestamp(word, start, end));
            }
        }
        return new SpeechResult(RepairWavHeader(Convert.FromBase64String(encodedAudio)), timestamps, Exact: true);
    }

    private async Task PlaySpeechAsync(byte[] audio, IReadOnlyList<SpeechCue> cues, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Kokoro playback currently requires Windows.");
        string audioPath = Path.Combine(Path.GetTempPath(), $"neuroma-{Guid.NewGuid():N}.wav");
        MciWavePlayer? player = null;
        try
        {
            await File.WriteAllBytesAsync(audioPath, audio, cancellationToken).ConfigureAwait(false);
            await WaitWhilePausedAsync(cancellationToken).ConfigureAwait(false);
            player = new MciWavePlayer(audioPath);
            player.Play();
            lock (_gate)
            {
                _activePlayer = player;
                if (_isPaused) player.TryPause();
            }

            using CancellationTokenRegistration registration = cancellationToken.Register(player.TryStop);
            int activeIndex = -1;
            while (!player.HasEnded && !cancellationToken.IsCancellationRequested)
            {
                double elapsed = player.PositionSeconds;
                int cueIndex = FindActiveCue(cues, elapsed);
                if (cueIndex >= 0 && cueIndex != activeIndex)
                {
                    activeIndex = cueIndex;
                    SetCue(cues[cueIndex]);
                }
                await Task.Delay(25, cancellationToken).ConfigureAwait(false);
            }
            cancellationToken.ThrowIfCancellationRequested();
        }
        finally
        {
            lock (_gate)
                if (ReferenceEquals(_activePlayer, player)) _activePlayer = null;
            player?.Dispose();
            SetCue(null);
            try { File.Delete(audioPath); } catch (IOException) { }
        }
    }

    private async Task WaitWhilePausedAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            Task? resume;
            lock (_gate) resume = _isPaused ? _resumeSignal?.Task : null;
            if (resume is null) return;
            await resume.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task DelayRespectingPauseAsync(int milliseconds, CancellationToken cancellationToken)
    {
        int remaining = Math.Max(0, milliseconds);
        while (remaining > 0)
        {
            await WaitWhilePausedAsync(cancellationToken).ConfigureAwait(false);
            int slice = Math.Min(remaining, 50);
            await Task.Delay(slice, cancellationToken).ConfigureAwait(false);
            lock (_gate)
                if (!_isPaused) remaining -= slice;
        }
    }

    private static int FindActiveCue(IReadOnlyList<SpeechCue> cues, double elapsed)
    {
        int last = -1;
        for (int index = 0; index < cues.Count; index++)
        {
            if (elapsed >= cues[index].Start) last = index;
            if (elapsed >= cues[index].Start && elapsed < cues[index].End) return index;
        }
        return last;
    }

    private static IReadOnlyList<SpeechCue> BuildCues(SpeechChunk chunk, IReadOnlyList<SpeechTimestamp> timestamps)
    {
        var cues = new List<SpeechCue>(); int searchFrom = 0;
        foreach (SpeechTimestamp timestamp in timestamps)
        {
            string word = TrimNonWord().Replace(timestamp.Word, ""); if (word.Length == 0) continue;
            int matchStart = chunk.Text.IndexOf(word, searchFrom, StringComparison.CurrentCultureIgnoreCase);
            int matchLength = word.Length;
            if (matchStart < 0)
            {
                Match fallback = SpokenWord().Match(chunk.Text, searchFrom);
                if (!fallback.Success) continue;
                matchStart = fallback.Index; matchLength = fallback.Length;
            }
            int matchEnd = matchStart + matchLength; searchFrom = matchEnd;
            SpeechSpan? span = chunk.Spans.FirstOrDefault(candidate => matchStart >= candidate.TextStart && matchStart < candidate.TextEnd);
            if (span is null || matchEnd > span.TextEnd) continue;
            int relativeStart = matchStart - span.TextStart;
            int relativeEnd = matchEnd - span.TextStart;
            int columnStart = span.ColumnMap is { } map && relativeStart < map.Count
                ? map[relativeStart] : span.VisibleStart + relativeStart;
            int columnEnd = span.ColumnMap is { } endMap && relativeEnd < endMap.Count
                ? endMap[relativeEnd] : span.VisibleStart + relativeEnd;
            cues.Add(new SpeechCue(word, timestamp.StartTime, timestamp.EndTime, span.ChapterIndex, span.LineIndex,
                columnStart, columnEnd));
        }
        return cues;
    }

    private static IReadOnlyList<SpeechTimestamp> EstimateTimestamps(string text, double duration)
    {
        MatchCollection words = SpokenWord().Matches(text); if (words.Count == 0) return [];
        double[] weights = words.Select(match => Math.Max(1, Math.Sqrt(match.Length))).ToArray();
        double total = weights.Sum(); double elapsed = 0; var result = new List<SpeechTimestamp>();
        for (int index = 0; index < words.Count; index++)
        {
            double start = elapsed; elapsed += duration * weights[index] / total;
            result.Add(new SpeechTimestamp(words[index].Value, start, elapsed));
        }
        return result;
    }

    private static byte[] RepairWavHeader(byte[] audio)
    {
        if (audio.Length < 44 || Encoding.ASCII.GetString(audio, 0, 4) != "RIFF" || Encoding.ASCII.GetString(audio, 8, 4) != "WAVE")
            return audio;
        byte[] repaired = audio.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(repaired.AsSpan(4, 4), (uint)(repaired.Length - 8));
        int offset = 12;
        while (offset + 8 <= repaired.Length)
        {
            string name = Encoding.ASCII.GetString(repaired, offset, 4);
            uint declaredSize = BinaryPrimitives.ReadUInt32LittleEndian(repaired.AsSpan(offset + 4, 4));
            if (name == "data")
            {
                BinaryPrimitives.WriteUInt32LittleEndian(repaired.AsSpan(offset + 4, 4), (uint)(repaired.Length - offset - 8));
                break;
            }
            if (declaredSize == uint.MaxValue || declaredSize > int.MaxValue) break;
            long next = offset + 8L + declaredSize + declaredSize % 2;
            if (next > repaired.Length) break;
            offset = (int)next;
        }
        return repaired;
    }

    private static double WavDuration(byte[] audio)
    {
        if (audio.Length < 44) return 0;
        uint byteRate = BinaryPrimitives.ReadUInt32LittleEndian(audio.AsSpan(28, 4));
        return byteRate == 0 ? 0 : Math.Max(0, audio.Length - 44d) / byteRate;
    }

    private void SetStatus(string status)
    {
        lock (_gate)
        {
            if (_isPaused) _resumeStatus = status;
            else _status = status;
        }
        Changed?.Invoke();
    }

    private void SetCue(SpeechCue? cue)
    {
        lock (_gate) _currentCue = cue;
        Changed?.Invoke();
    }

    private sealed class MciWavePlayer : IDisposable
    {
        private readonly string _alias = "neuroma" + Guid.NewGuid().ToString("N");
        private readonly object _commandLock = new();
        private bool _opened;

        public MciWavePlayer(string path)
        {
            if (path.Contains('"'))
                throw new ArgumentException("Audio path contains an unsupported quote.", nameof(path));
            Send($"open \"{path}\" type waveaudio alias {_alias}");
            _opened = true;
            try { Send($"set {_alias} time format milliseconds"); }
            catch { Dispose(); throw; }
        }

        public double PositionSeconds
        {
            get
            {
                string value = Query($"status {_alias} position");
                return double.TryParse(value, out double milliseconds) ? milliseconds / 1000 : 0;
            }
        }

        public bool HasEnded => string.Equals(Query($"status {_alias} mode"), "stopped", StringComparison.OrdinalIgnoreCase);

        public void Play() => Send($"play {_alias}");
        public void TryPause() => TrySend($"pause {_alias}");
        public void TryResume() => TrySend($"resume {_alias}");
        public void TryStop() => TrySend($"stop {_alias}");

        public void Dispose()
        {
            lock (_commandLock)
            {
                if (!_opened) return;
                MciSendString($"close {_alias}", null, 0, nint.Zero);
                _opened = false;
            }
        }

        private string Query(string command)
        {
            var result = new StringBuilder(128);
            Send(command, result);
            return result.ToString().Trim();
        }

        private void Send(string command, StringBuilder? result = null)
        {
            lock (_commandLock)
            {
                int code = MciSendString(command, result, result?.Capacity ?? 0, nint.Zero);
                if (code == 0) return;
                var message = new StringBuilder(256);
                MciGetErrorString(code, message, message.Capacity);
                throw new InvalidOperationException($"Windows audio playback failed: {message.ToString().Trim()}");
            }
        }

        private void TrySend(string command)
        {
            lock (_commandLock)
            {
                if (_opened) MciSendString(command, null, 0, nint.Zero);
            }
        }

        [DllImport("winmm.dll", CharSet = CharSet.Unicode, EntryPoint = "mciSendStringW")]
        private static extern int MciSendString(string command, StringBuilder? result, int resultLength, nint callback);

        [DllImport("winmm.dll", CharSet = CharSet.Unicode, EntryPoint = "mciGetErrorStringW")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool MciGetErrorString(int errorCode, StringBuilder errorText, int errorTextSize);
    }

    [GeneratedRegex(@"^[^\p{L}\p{N}]+|[^\p{L}\p{N}]+$")]
    private static partial Regex TrimNonWord();

    [GeneratedRegex(@"[\p{L}\p{N}]+(?:['’][\p{L}\p{N}]+)*")]
    private static partial Regex SpokenWord();
}
