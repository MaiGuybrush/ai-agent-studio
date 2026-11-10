using System;
using System.Globalization;
namespace AiAgentStudio.Api.Error;
//ApiErr includes http status code and error code
public enum ApiErr {
    Unknown = 0,
    Ok = 200,
    NoContent = 204,
    BadRequest = 400,
    Unauthorized = 401,
    Forbidden = 403,
    NotFound = 404,
    InternalServerError = 500,
    CustomError1 = 1001,
}
public class ApiException : Exception
{
    public ApiErr _err = (int)ApiErr.Unknown;
    public ApiErr Err {get {
        return _err;
    }}
    public ApiException() : base() {}

    public ApiException(ApiErr err, string message, params object[] args) : base(message) {
        _err = err;
    }

    public ApiException(string message, params object[] args) 
        : base(String.Format(CultureInfo.CurrentCulture, message, args))
    {
    }
}