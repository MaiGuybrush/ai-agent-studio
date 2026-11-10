using System;
using System.Linq;

namespace AiAgentStudio.Api.Identity;

/// <summary>CIM 部門代碼設定（spec 3.2）。設定節：Cim。</summary>
public sealed class CimOptions
{
    /// <summary>CIM 部門代碼清單；以部門代碼的前 N 碼（N = 該項字數）、區分大小寫比對。空清單 = 無人是 CIM Engineer。</summary>
    public string[] DeptCodePrefixes { get; set; } = Array.Empty<string>();

    /// <summary>含空字串（或純空白）的項目會讓啟動失敗，否則 "" 會讓所有人都符合。</summary>
    public bool IsValid => DeptCodePrefixes.All(p => !string.IsNullOrWhiteSpace(p));

    public bool IsCimDept(string? deptCode) =>
        !string.IsNullOrEmpty(deptCode) && DeptCodePrefixes.Any(p => deptCode.StartsWith(p, StringComparison.Ordinal));
}
