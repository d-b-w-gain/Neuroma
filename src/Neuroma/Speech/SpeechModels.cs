namespace Neuroma.Speech;

public sealed record SpeechSpan(
    int TextStart,
    int TextEnd,
    int ChapterIndex,
    int LineIndex,
    int VisibleStart,
    IReadOnlyList<int>? ColumnMap = null);

public sealed record SpeechChunk(string Text, IReadOnlyList<SpeechSpan> Spans);

public sealed record SpeechCue(
    string Word,
    double Start,
    double End,
    int ChapterIndex,
    int LineIndex,
    int ColumnStart,
    int ColumnEnd);

internal sealed record SpeechTimestamp(string Word, double StartTime, double EndTime);
internal sealed record SpeechResult(byte[] Audio, IReadOnlyList<SpeechTimestamp> Timestamps, bool Exact);
