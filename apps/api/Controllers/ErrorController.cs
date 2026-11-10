using System;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using AiAgentStudio.Api.Error;

namespace AiAgentStudio.Api.Controllers;
[ApiController]
[AllowAnonymous]
[ApiExplorerSettings(IgnoreApi = true)]    
public class ErrorController : ControllerBase
{
    [Route("/error-local-development")]
    public IActionResult ErrorLocalDevelopment(
            [FromServices] IWebHostEnvironment webHostEnvironment)
    {
        if (webHostEnvironment.EnvironmentName != "Development")
        {
            throw new InvalidOperationException(
                "This shouldn't be invoked in non-development environments.");
        }

        var context = HttpContext.Features.Get<IExceptionHandlerFeature>();
        if (context?.Error is ApiException ex) {
            if ((int)ex.Err < 1000) {
                return new ContentResult() {
                    StatusCode = (int)ex.Err,
                    Content = ex.Message
                };
            } 
        }
        return Problem(
            detail: context?.Error?.StackTrace ?? context?.Error?.Message ?? "An unexpected error occurred.",
            title: context?.Error?.Message ?? "Internal Server Error");
    }

    [Route("/error")]
    public IActionResult Error()
    {
        var context = HttpContext.Features.Get<IExceptionHandlerFeature>();
        if (context?.Error is ApiException ex) {
            if ((int)ex.Err < 1000) {
                return new ContentResult() {
                    StatusCode = (int)ex.Err,
                    Content = ex.Message
                };
            } 
        }
        return Problem(
            detail: context?.Error?.Message ?? "An unexpected error occurred.",
            title: context?.Error?.Message ?? "Internal Server Error");
    }
}
