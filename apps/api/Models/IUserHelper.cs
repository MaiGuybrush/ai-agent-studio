namespace AiAgentStudio.Api.Models;

public interface IUserHelper {
    UserDTO Authenticate(string shop, string userId, string password);

    UserDTO GetUser(string shop, string id, bool includeGroup = false);

    UserFunctionDTO GetUserFunctions(string shop, string userId);

    int CheckAuthorization(string shop, string userId, string funcid, string subSystem);

    bool IsUserInGroup(string shop, string userId, string groupId);

    /// <summary>
    /// get users by page(optional)
    /// </summary>
    /// <param name="shop"></param>
    /// <param name="page">page number(from 0), if page == -1, get all records</param>
    /// <param name="pageSize">page size</param>
    /// <returns></returns>
    UserDTO[] GetUsers(string shop, int page, int pageSize);
    
    void AddUsers(string shop, UserDTO[] usersDTO, string claimUser);

    void AddUpdateUsers(string shop, UserDTO[] usersDTO, string claimUser);
    
    void DeleteUsers(string shop, string[] userIds, string claimUser);
}
