using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AiAgentStudio.Api.Interviews;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AiAgentStudio.Api.Vocabulary;

/// <summary>agent 的 naming_check 工具（spec 8.3）：欄位名稱清單（含巢狀子欄位的名稱）→ 每個名稱的狀態與建議。完全沒有詞彙快取時回 503。</summary>
[ApiController]
[Route("internal/tools/naming-check")]
[AllowAnonymous]
public class NamingCheckToolController(RunTokenService tokens, VocabularyService vocab, PendingTermService pendingTerms) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Check([FromQuery] string[] fields, CancellationToken ct)
    {
        if (tokens.Validate(Request.Headers["X-Run-Token"].ToString()) is null) return Unauthorized();
        VocabularySnapshot v;
        try { v = await vocab.GetAsync(ct); }
        catch (VocabularyException e) { return StatusCode(503, new { message = ToolSpecNaming.NoCache(e.Message) }); }
        var pending = (await pendingTerms.ActiveAsync(v.Entries, ct)).Select(x => x.Term).ToList();
        return Ok(new
        {
            sha = v.Sha, fetchedAt = v.FetchedAt, warning = v.Warning,
            results = fields.Select(f => { var r = NamingChecker.Check(v.Entries, f, pending: pending); return new { field = f, status = r.Status, suggestion = r.Suggestion }; }),
        });
    }
}
