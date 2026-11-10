namespace AiAgentStudio.Api.Services
{
    /// <summary>
    /// 當前呼叫者身分服務 (Caller Identity)
    /// </summary>
    public interface ICurrentUserService
    {
        /// <summary>
        /// 員工帳號 (來源為 empID claim，可回退到 NameIdentifier，未登入或無此 claim 時為 null)
        /// </summary>
        string EmpId { get; }

        /// <summary>
        /// 員工編號 (來源為 empNo claim，未登入或無此 claim 時為 null)
        /// </summary>
        string EmpNo { get; }

        /// <summary>
        /// 員工姓名 (來源為 empName claim，可回退到 Name，未登入或無此 claim 時為 null)
        /// </summary>
        string EmpName { get; }

        /// <summary>
        /// 認證方式 (來源為 authMethod claim，可回退到認證類型，未登入或無認證時為 null)
        /// </summary>
        string AuthMethod { get; }
    }
}
