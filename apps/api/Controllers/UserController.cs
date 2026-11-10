using System.Linq;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Authorization;
using System;
using ClosedXML.Excel;
using System.IO;
using Microsoft.Extensions.Configuration;
using System.Text.Json;
using System.Net.Http;
using AiAgentStudio.Api.Models;
using AiAgentStudio.Api.Services;

namespace AiAgentStudio.Api.Controllers
{
    [ApiController]
    [Route("{urlshop}/[controller]")]
    public class UserController : ControllerBase
    {
        private readonly IWebHostEnvironment _hostingEnvironment;

        private readonly ILogger<UserController> _logger;

        private readonly IUserHelper _userHelper;

        private readonly ICurrentUserService _currentUserService;

        public UserController(ILogger<UserController> logger, IWebHostEnvironment hostingEnvironment, 
                    IUserHelper userHelper, ICurrentUserService currentUserService)
        {
            _hostingEnvironment = hostingEnvironment;
            _logger = logger;
            _userHelper = userHelper;
            _currentUserService = currentUserService;
        }


        [HttpGet]
        [Route("JwtTest")]
        [ProducesResponseType(200, Type = typeof(string))]
        [ProducesResponseType(500)]
        [Authorize]
        public IActionResult JwtTest()
        {
            return Ok($"JWT test successful. AuthInfo: {_currentUserService.EmpNo}. your login info EmpID: {_currentUserService.EmpId}, EmpNo: {_currentUserService.EmpNo}, EmpName: {_currentUserService.EmpName}, AuthMethod: {_currentUserService.AuthMethod}");
        }

        [HttpGet]
        [Route("")]
        [ProducesResponseType(200, Type = typeof(UserDTO[]))]
        [ProducesResponseType(500)]
        public IActionResult GetUsers(string urlshop, [FromQuery]int page, [FromQuery]int pageSize)
        {    
            return Ok(_userHelper.GetUsers(urlshop, page, pageSize));
        }
    }
}
