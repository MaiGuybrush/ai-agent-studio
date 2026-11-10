namespace AiAgentStudio.Api.Models;
public class UserFunctionDTO {
        public string UserId { get; set; }

        public string UserName { get; set; }

        public string Dept { get; set; }

        public GroupFunctionDTO[] Functions{ get; set; }
}
