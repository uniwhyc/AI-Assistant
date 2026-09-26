using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace AI_Assistant
{
    public static class CsvExport
    {
        static string Cell(string value)
        {
            value = value ?? "";
            string trimmed = value.TrimStart();
            if (trimmed.Length > 0 && "=+-@".Contains(trimmed[0])) value = "'" + value;
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        }
        public static void Write(TextWriter writer, IEnumerable<SessionRow> rows)
        {
            writer.WriteLine("平台,会话,项目,模型,最近活动（本地时间）,普通输入,缓存读取,缓存写入,输出,总Token,缓存命中率");
            foreach (var row in rows)
                writer.WriteLine(string.Join(",", new[] { row.Platform, row.Session, row.ProjectPath, row.Model,
                    row.Last.ToString("yyyy-MM-dd HH:mm:ss"), row.Input.ToString(CultureInfo.InvariantCulture),
                    row.CacheRead.ToString(CultureInfo.InvariantCulture), row.CacheWrite.ToString(CultureInfo.InvariantCulture),
                    row.Output.ToString(CultureInfo.InvariantCulture), row.Total.ToString(CultureInfo.InvariantCulture), row.HitRate }.Select(Cell)));
        }
    }
}
