using System.IO;
using System.Text;

namespace Xiaoliu.App;

/// <summary>
/// 极简文件日志。常驻进程出问题时，用户唯一能给你的就是日志，
/// 所以每条都带时间戳，按天切文件，写完立刻落盘（崩溃时不能丢最后几行）。
/// </summary>
public static class Log
{
    private static readonly object Gate = new();
    private static readonly string Directory =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "xiaoliu", "logs");

    /// <summary>日志目录。报错弹窗里要给用户看，不然用户不知道去哪儿翻。</summary>
    public static string LogDirectory => Directory;

    public static void Info(string area, string message) => Write("INFO", area, message);

    public static void Error(string area, Exception exception) =>
        Write("ERROR", area, exception.ToString());

    public static void Error(string area, string message) => Write("ERROR", area, message);

    private static void Write(string level, string area, string message)
    {
        try
        {
            lock (Gate)
            {
                System.IO.Directory.CreateDirectory(Directory);
                var path = Path.Combine(Directory, $"xiaoliu-{DateTime.Now:yyyyMMdd}.log");
                var line = new StringBuilder()
                    .Append(DateTime.Now.ToString("HH:mm:ss.fff"))
                    .Append(' ').Append(level)
                    .Append(" [").Append(area).Append("] ")
                    .Append(message)
                    .AppendLine()
                    .ToString();
                File.AppendAllText(path, line, Encoding.UTF8);
            }
        }
        catch (Exception)
        {
            // 日志写不进去也不能把应用带崩。
        }
    }
}
