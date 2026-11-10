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
using Swashbuckle.AspNetCore.Annotations;
using AiAgentStudio.Api.Services;

namespace AiAgentStudio.Api.Controllers
{
    [ApiController]
    [Route("{shop}/[controller]")]
    public class ConfigController : ControllerBase
    {
        private readonly IWebHostEnvironment _hostingEnvironment;

        private readonly ILogger<ConfigController> _logger;

        private readonly ICurrentUserService _currentUserService;

        public ConfigController(ILogger<ConfigController> logger, IWebHostEnvironment hostingEnvironment, 
                    IConfiguration configuration, ICurrentUserService currentUserService)
        {
            _hostingEnvironment = hostingEnvironment;
            Configuration = configuration;
            _logger = logger;
            _currentUserService = currentUserService;
        }

        public IConfiguration Configuration { get; }

        [HttpGet]
        [Route("JwtTest")]
        [SwaggerResponse(200, "ok", typeof(string), new string[] { "application/json" })]
        [SwaggerResponse(500, "error", typeof(string), new string[] { "text/plain" })]
        [Authorize]
        public IActionResult JwtTest()
        {
            return Ok($"JWT test successful. AuthInfo: {_currentUserService.EmpNo}. your login info EmpID: {_currentUserService.EmpId}, EmpNo: {_currentUserService.EmpNo}, EmpName: {_currentUserService.EmpName}, AuthMethod: {_currentUserService.AuthMethod}");
        }

        [HttpGet]
        [Route("")]
        [SwaggerResponse(200, "ok", typeof(string), new string[] { "application/json" })]
        [SwaggerResponse(400, "bad request", typeof(string), new string[] { "text/plain" })]  
        [SwaggerResponse(500, "error", typeof(string), new string[] { "text/plain" })]
        public IActionResult Hello()
        {    
            return Ok($"Hello");
        }

        /// <summary>
        /// 無任何 [Authorize] 屬性的測試端點，用以驗證 RequireAuth 為 true 時 FallbackPolicy 生效回 401
        /// </summary>
        [HttpGet]
        [Route("UnprotectedTest")]
        [SwaggerResponse(200, "ok", typeof(string), new string[] { "application/json" })]
        [SwaggerResponse(401, "unauthorized", typeof(string), new string[] { "text/plain" })]
        [SwaggerResponse(500, "error", typeof(string), new string[] { "text/plain" })]
        public IActionResult UnprotectedTest()
        {
            return Ok("Unprotected endpoint accessed successfully.");
        }
    }
}
