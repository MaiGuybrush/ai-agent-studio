namespace AiAgentStudio.Api.Models;

public class UserHelper1 : IUserHelper
{
    public void AddUpdateUsers(string shop, UserDTO[] usersDTO, string claimUser)
    {
        throw new System.NotImplementedException();
    }

    public void AddUsers(string shop, UserDTO[] usersDTO, string claimUser)
    {
        throw new System.NotImplementedException();
    }

    public UserDTO Authenticate(string shop, string userId, string password)
    {
        throw new System.NotImplementedException();
    }

    public int CheckAuthorization(string shop, string userId, string funcid, string subSystem)
    {
        throw new System.NotImplementedException();
    }

    public void DeleteUsers(string shop, string[] userIds, string claimUser)
    {
        throw new System.NotImplementedException();
    }

    public UserDTO GetUser(string shop, string id, bool includeGroup = false)
    {
        throw new System.NotImplementedException();
    }

    public UserFunctionDTO GetUserFunctions(string shop, string userId)
    {
        throw new System.NotImplementedException();
    }

    public UserDTO[] GetUsers(string shop, int page, int pageSize)
    {
        return new UserDTO[] { 
            new UserDTO() {
                UserId = "1",
                UserName = "User1",
            },
            new UserDTO() {
                UserId = "2",
                UserName = "User2",
            }
        };
    }

    public bool IsUserInGroup(string shop, string userId, string groupId)
    {
        throw new System.NotImplementedException();
    }
}