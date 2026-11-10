using System.Linq;
using AutoMapper;
using AiAgentStudio.Api.Models;

public class AutoMappingProfile : Profile
{
    public AutoMappingProfile()
    {
        // AllowNullCollections = true;
        // CreateMap<Department, DepartmentDTO>().ReverseMap();
        // CreateMap<HrEmployee, HrEmployeeDTO>();
        // CreateMap<GroupFunction, GroupFunctionDTO>()
        // .AfterMap((s, d) => {
        //     if (s.FunctionInfo != null) {
        //         d.FunctionName = s.FunctionInfo.FunctionName;
        //     }
        // });
        // CreateMap<GroupFunction, FunctionGroupDTO>();
        // CreateMap<UserGroup, UserSimpleDTO>();
        // CreateMap<UserGroup, UserGroupDTO>();
        // CreateMap<Group, GroupDTO>()
        // .ForMember(g => g.Functions, opt => opt.Ignore())
        // .ForMember(g => g.Users, opt => opt.Ignore())
        // .AfterMap((s, d) => {
        //     if (s.Functions != null) {
        //         d.Functions = s.Functions.Select(f => new GroupFunctionDTO() {
        //             SubSystem = f.SubSystem,
        //             FunctionId = f.FunctionId,
        //             FunctionName = f.FunctionInfo != null ? f.FunctionInfo.FunctionName : "",
        //             AccessFlag = f.AccessFlag
        //         }).ToList();
        //     }
        //     if (s.Users != null) {
        //         d.Users = s.Users.Select(u => new UserSimpleDTO() {
        //             UserId = u.UserId,
        //             UserName = u.UserInfo != null ? u.UserInfo.UserName : "",
        //             Dept = u.UserInfo.Dept,
        //             NotesId = u.UserInfo.NotesId
        //         }).ToList();
        //     }
        // });
        // CreateMap<User, UserDTO>();
        // CreateMap<SystemFunction, FunctionDTO>()
        // .ForMember(g => g.Groups, opt => opt.Ignore())
        // .AfterMap((s, d) => {
        //     if (s.Groups != null) {
        //         d.Groups = s.Groups.Select(g => new FunctionGroupDTO() {
        //             GroupId = g.GroupId, 
        //             GroupName = g.GroupInfo.GroupName,
        //             AccessFlag = g.AccessFlag
        //         }).ToList();
        //     }
        // });
        


        // CreateMap<UserDTO, User>()
        // .ForMember(u => u.Groups, opt => opt.Ignore())
        // .ForMember(u => u.UserId, opt => opt.Condition((src, dest) => {
        //     return dest.UserId?.Trim() != src.UserId?.Trim();
        // }))
        // .AfterMap((s, d) => {
        //     if (s.Groups != null) {
        //         d.Groups = s.Groups.Select(g => new UserGroup() {
        //             GroupId = g.GroupId,
        //             GroupName = g.GroupName,
        //             UserId = s.UserId
        //         }).ToList();
        //     }
        // });
        // CreateMap<GroupDTO, Group>()
        // .ForMember(g => g.Functions, opt => opt.Ignore())
        // .ForMember(g => g.Users, opt => opt.Ignore())
        // .ForMember(u => u.GroupId, opt => opt.Condition((src, dest) => {
        //     return dest.GroupId?.Trim() != src.GroupId?.Trim();
        // }))
        // .AfterMap((s, d) => {
        //     if (s.Functions != null) {
        //         d.Functions = s.Functions.Select(f => new GroupFunction() {
        //             GroupId = d.GroupId,
        //             SubSystem = f.SubSystem,
        //             FunctionId = f.FunctionId,
        //             AccessFlag = f.AccessFlag
        //         }).ToList();
        //     }
        //     if (s.Users != null) {
        //         d.Users = s.Users.Select(u => new UserGroup() {
        //             GroupId = d.GroupId,
        //             GroupName = d.GroupName,
        //             UserId = u.UserId
        //         }).ToList();
        //     }
        // });

        // CreateMap<FunctionDTO, SystemFunction>()
        // .ForMember(f => f.Groups, opt => opt.Ignore())
        // .ForMember(f => f.SubSystem, opt => opt.Condition((src, dest) => {
        //     return dest.SubSystem?.Trim() != src.SubSystem?.Trim();
        // }))
        // .ForMember(f => f.FunctionId, opt => opt.Condition((src, dest) => {
        //     return dest.FunctionId?.Trim() != src.FunctionId?.Trim();
        // }))
        // .AfterMap((s, d) => {
        //     if (s.Groups != null) {
        //         d.Groups = s.Groups.Select(g => new GroupFunction() {
        //             GroupId = g.GroupId,
        //             FunctionId = s.FunctionId,
        //             SubSystem = s.SubSystem,
        //             AccessFlag = g.AccessFlag
        //         }).ToList();
        //     }
        // });

    }
}