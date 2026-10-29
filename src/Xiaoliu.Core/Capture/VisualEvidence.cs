namespace Xiaoliu.Core.Capture;

/// <summary>
/// 本轮可用的视觉证据：只来自用户圈选，模型无法自己加。
///
/// 有效性不看时间看内容（docs/adr/0003）：截图之后只要那块屏幕没变，就一直可用；
/// 变了就判 stale_capture，让模型知道「你手上那张图已经过时」。
/// </summary>
public sealed class VisualEvidence
{
    public sealed record Shot(
        string CaptureCallId,
        int ScreenNumber,
        CaptureRect Rect,
        CapturedImage Image,
        DateTimeOffset CapturedAt);

    private readonly List<Shot> _shots = new();
    private readonly Func<CaptureRect, byte[], bool> _stillMatches;
    private int _sequence;

    /// <param name="stillMatches">判定「那块屏幕有没有变」。默认真的去截一次图算 hash；测试里可以换成桩。</param>
    public VisualEvidence(Func<CaptureRect, byte[], bool>? stillMatches = null) =>
        _stillMatches = stillMatches ?? ScreenCapture.StillMatches;

    public IReadOnlyList<Shot> Shots => _shots;

    public bool IsEmpty => _shots.Count == 0;

    /// <summary>最近一次圈选，指点工具默认引用它。</summary>
    public Shot? Latest => _shots.Count == 0 ? null : _shots[^1];

    /// <summary>登记一张用户圈选图，返回它在工具回执里使用的引用号。</summary>
    public Shot Add(CaptureRect rect, CapturedImage image)
    {
        var shot = new Shot($"selection-{++_sequence}", 1, rect, image, DateTimeOffset.UtcNow);
        _shots.Add(shot);
        return shot;
    }

    /// <summary>
    /// 登记一张用户从磁盘选来的图。它不是抓屏来的，没有「那块屏幕变了没有」这回事，
    /// 所以 Rect 留零、判活时直接放行 —— 否则下一次 Prune 会把它当过期图删掉。
    /// </summary>
    public Shot AddImage(CapturedImage image)
    {
        var shot = new Shot($"selection-{++_sequence}", 1, default, image, DateTimeOffset.UtcNow);
        _shots.Add(shot);
        return shot;
    }

    public bool TryGet(string captureCallId, int screenNumber, out Shot shot)
    {
        shot = _shots.FirstOrDefault(s =>
            string.Equals(s.CaptureCallId, captureCallId, StringComparison.Ordinal) &&
            s.ScreenNumber == screenNumber)!;
        return shot is not null;
    }

    /// <summary>屏幕内容变了没有。变了就是 stale，不是过期。</summary>
    public bool StillValid(Shot shot)
    {
        if (shot.Image.Signature is not { Length: > 0 } signature)
        {
            return false;
        }

        // 零矩形 = 用户选的图，不是抓屏来的，没有可比对的屏幕区域。
        return shot.Rect.Width <= 0 || shot.Rect.Height <= 0 || _stillMatches(shot.Rect, signature);
    }

    /// <summary>下一轮对话开始时清空（点点的老截图引用不能跨轮复用）。</summary>
    public void Clear()
    {
        _shots.Clear();
        _sequence = 0;
    }

    /// <summary>
    /// 丢掉已经过时的截图，返回丢了几个。
    ///
    /// 跨轮保留是刻意的：截图的有效期由内容决定，不由「上一轮结束」决定。
    /// 用户盯着同一块界面连问三句，不该每句都重新圈选。
    /// </summary>
    public int Prune()
    {
        var stale = _shots.Where(shot => !StillValid(shot)).ToList();
        foreach (var shot in stale)
        {
            _shots.Remove(shot);
        }

        return stale.Count;
    }

    /// <summary>给模型看的画面来源说明，直接进 RuntimeContext。</summary>
    public string DescribeForModel()
    {
        if (_shots.Count == 0)
        {
            return "本轮没有附带屏幕截图，你看不到他当前的界面。小柳不允许模型自主抓屏："
                   + "不要调用 capture_screen，也不要调用指点工具；涉及当前画面的问题请说明看不到，或请用户圈选后重问。";
        }

        var lines = _shots.Select(s =>
            $"{s.CaptureCallId}，screen_number={s.ScreenNumber}，{s.Image.Width}x{s.Image.Height} 像素，"
            + $"覆盖屏幕区域 ({s.Rect.X},{s.Rect.Y}) {s.Rect.Width}x{s.Rect.Height}");
        return "本轮附带的截图来自用户主动圈选，不是模型抓取。可用引用："
               + string.Join("；", lines)
               + "。指点工具必须引用这些 capture_call_id 与 screen_number，坐标按 0-1000 归一化。";
    }
}
