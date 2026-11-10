using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;

namespace AiAgentStudio.Api.Submissions;

public sealed record RevisionChange(string Path, JsonNode? Before, JsonNode? After);

/// <summary>兩個 revision 內容的差異（spec 6.5）：物件欄位遞迴到葉節點（陣列、純量整個比），路徑以 . 連接。</summary>
public static class RevisionDiff
{
    public static List<RevisionChange> Between(string beforeJson, string afterJson)
    {
        var res = new List<RevisionChange>();
        Walk("", JsonNode.Parse(beforeJson), JsonNode.Parse(afterJson), res);
        return res;
    }

    static void Walk(string path, JsonNode? a, JsonNode? b, List<RevisionChange> res)
    {
        if (a is JsonObject oa && b is JsonObject ob)
            foreach (var k in oa.Select(p => p.Key).Union(ob.Select(p => p.Key)))
                Walk(path == "" ? k : $"{path}.{k}", oa[k], ob[k], res);
        else if (!JsonNode.DeepEquals(a, b)) res.Add(new(path, a?.DeepClone(), b?.DeepClone()));
    }
}
