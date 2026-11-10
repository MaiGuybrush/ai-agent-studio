
using System;
using System.Collections.Generic;

namespace AiAgentStudio.Api.Models;

public class UserDTO
{
    public string UserId { get; set; }

    public string UserName { get; set; }            

    public string ChineseName { get; set; }

    public string Dept { get; set; }

    public string DeptName { get; set; }

    public string Title { get; set; }

    public string ExtNo { get; set; }

    public string Password { get; set; }

    public string NotesId { get; set; }

    public string BrmRelease { get; set; }

    public string BrmSuper { get; set; }

    public string ResetFlg { get; set; }

    public string ExpireFlg { get; set; }

    public DateTime? ExpireDate { get; set; }

    public string CmoUserId { get; set; }

    public string DisableFlg { get; set; }

    public string AdminFlag { get; set; }
    
    // public List<UserGroupDTO> Groups { get; set; }
    public UserDTO()
    {
    }
}