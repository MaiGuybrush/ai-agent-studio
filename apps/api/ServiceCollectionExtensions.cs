using Microsoft.EntityFrameworkCore;
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using Swashbuckle.AspNetCore.SwaggerGen;
using Consul;
using AiAgentStudio.Api.Models;
using AiAgentStudio.Api.Services;

namespace AiAgentStudio.Api
{
    public static class ServiceCollectionExtensions
    {
        /// <summary>
        /// 註冊應用程式服務
        /// </summary>
        public static IServiceCollection AddApplicationServices(this IServiceCollection services, IConfiguration configuration)
        {
            services.AddAutoMapper(typeof(AutoMappingProfile));
            services.AddScoped<IUserHelper, UserHelper1>();

            services.AddControllers()
                .AddJsonOptions(options =>
                {
                    options.JsonSerializerOptions.Converters.Add(new TrimConverter());
                });

            return services;
        }

        /// <summary>
        /// 註冊基礎設施服務
        /// </summary>
        public static IServiceCollection AddInfrastructureServices(this IServiceCollection services, IConfiguration configuration)
        {
            // NoteMiddleware Options (Shops)
            var shops = configuration.GetSection("Shops").Get<string[]>() ?? Array.Empty<string>();
            services.Configure<NoteMiddlewareOptions>(options =>
            {
                options.AvailableShops = shops;
            });

            services.Configure<ApiSettings>(configuration.GetSection("AppSettings"));

            // 時間抽象 TimeProvider
            services.TryAddSingleton(TimeProvider.System);

            // 快取服務（支援 API Key 快取等需求）
            services.AddMemoryCache();

            // System Scope 驗證：RequireAuth 為 true 且 SystemName 為空時，應用程式啟動即失敗（程式碼不寫備援值）
            var requireAuth = configuration.GetValue<bool?>("Authentication:RequireAuth") ?? true;
            var systemName = configuration["Authentication:Uac:SystemName"]?.Trim();
            if (requireAuth && string.IsNullOrWhiteSpace(systemName))
            {
                throw new InvalidOperationException("Authentication:Uac:SystemName is required when Authentication:RequireAuth is true. Please configure Authentication:Uac:SystemName in appsettings.json.");
            }

            // 註冊 Consul 用戶端（單例）
            services.AddSingleton<IConsulClient>(sp =>
            {
                var uriStr = configuration["Consul:ServiceUri"];
                var serviceUri = !string.IsNullOrWhiteSpace(uriStr) && Uri.TryCreate(uriStr, UriKind.Absolute, out var uri)
                    ? uri
                    : new Uri("http://localhost:8500");

                if (!int.TryParse(configuration["Consul:TimeoutSeconds"], out var timeoutSec) || timeoutSec <= 0)
                {
                    timeoutSec = 5;
                }

                return new ConsulClient(
                    config =>
                    {
                        config.Address = serviceUri;
                        config.WaitTime = TimeSpan.FromSeconds(timeoutSec);
                    },
                    httpClient =>
                    {
                        httpClient.Timeout = TimeSpan.FromSeconds(timeoutSec);
                    });
            });

            // 註冊 Consul 服務位址供應模組（單例）
            // 本機開發：Authentication:Uac:BaseUrls 有值時直接使用（搭配 tools/mock-uac.mjs），不需要 Consul
            var uacBaseUrls = configuration.GetSection("Authentication:Uac:BaseUrls").Get<string[]>() ?? Array.Empty<string>();
            if (uacBaseUrls.Length > 0)
                services.AddSingleton<IConsulAddressProvider>(new AiAgentStudio.Api.Identity.StaticAddressProvider(uacBaseUrls));
            else
                services.AddSingleton<IConsulAddressProvider, ConsulAddressProvider>();

            // 身分與 CIM Engineer 判定（spec 3.1、3.2）；Cim:DeptCodePrefixes 含空字串時啟動失敗
            services.AddOptions<AiAgentStudio.Api.Identity.CimOptions>()
                .Bind(configuration.GetSection("Cim"))
                .Validate(o => o.IsValid, "Cim:DeptCodePrefixes must not contain empty strings.")
                .ValidateOnStart();
            services.AddSingleton<AiAgentStudio.Api.Identity.IHrClient, AiAgentStudio.Api.Identity.HrClient>();
            services.AddSingleton<AiAgentStudio.Api.Identity.IdentityService>();

            // 需求單：PostgreSQL（只有 C# 連線）；連線字串 ConnectionStrings:Studio
            services.AddSingleton<AiAgentStudio.Api.Tickets.IUserDirectory, AiAgentStudio.Api.Tickets.UacUserDirectory>();
            services.AddDbContext<AiAgentStudio.Api.Tickets.StudioDb>(o =>
                o.UseNpgsql(configuration.GetConnectionString("Studio")));

            // UAC 呼叫使用具名 HttpClient，逾時由 Authentication:Uac:TimeoutSeconds 控制（預設 10 秒）
            var uacTimeoutSeconds = configuration.GetValue<int?>("Authentication:Uac:TimeoutSeconds") ?? 10;
            if (uacTimeoutSeconds <= 0)
            {
                uacTimeoutSeconds = 10;
            }
            services.AddHttpClient("UacClient", client =>
            {
                client.Timeout = TimeSpan.FromSeconds(uacTimeoutSeconds);
            });

            // 註冊 UAC JWKS 公鑰供應服務（單例）
            services.AddSingleton<IJwksProvider, UacJwksProvider>();

            // 註冊 IHttpContextAccessor 與當前呼叫者身分服務 (ICurrentUserService，scoped)
            services.AddHttpContextAccessor();
            services.AddScoped<ICurrentUserService, CurrentUserService>();

            // 讀取 JWT 驗證組態
            var jwtSection = configuration.GetSection("Authentication:Jwt");
            var validateIssuer = jwtSection.GetValue<bool?>("ValidateIssuer") ?? false;
            var validateAudience = jwtSection.GetValue<bool?>("ValidateAudience") ?? false;
            var validateLifetime = jwtSection.GetValue<bool?>("ValidateLifetime") ?? true;
            var requireExpirationTime = jwtSection.GetValue<bool?>("RequireExpirationTime") ?? false;
            var clockSkewSeconds = jwtSection.GetValue<int?>("ClockSkewSeconds") ?? 0;
            var clockSkew = TimeSpan.FromSeconds(Math.Max(0, clockSkewSeconds));

            const string dynamicAuthScheme = "DynamicAuth";
            const string apiKeyScheme = ApiKeyAuthenticationOptions.DefaultScheme;

            // 雙軌認證：以 PolicyScheme 作為預設 scheme，依請求自動分流：
            // 有 X-API-Key 標頭，或 Authorization: Bearer <值> 且該值不是三段式 JWT，走 API Key；其餘走 JWT。
            services.AddAuthentication(auth =>
            {
                auth.DefaultScheme = dynamicAuthScheme;
                auth.DefaultAuthenticateScheme = dynamicAuthScheme;
                auth.DefaultChallengeScheme = dynamicAuthScheme;
            })
            .AddPolicyScheme(dynamicAuthScheme, "JWT or API Key", options =>
            {
                options.ForwardDefaultSelector = context =>
                {
                    // 1. 有 X-API-Key 標頭且非空值 -> 走 API Key
                    if (!string.IsNullOrWhiteSpace(context.Request.Headers["X-API-Key"]))
                    {
                        return apiKeyScheme;
                    }

                    // 2. Authorization: Bearer <值> 且該值不是三段式 JWT -> 走 API Key
                    var authHeader = context.Request.Headers.Authorization.ToString();
                    if (!string.IsNullOrWhiteSpace(authHeader) && authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                    {
                        var token = authHeader["Bearer ".Length..].Trim();
                        if (!string.IsNullOrEmpty(token) && !IsThreePartJwt(token))
                        {
                            return apiKeyScheme;
                        }
                    }

                    // 3. 其餘走 JWT
                    return JwtBearerDefaults.AuthenticationScheme;
                };
            })
            .AddJwtBearer(options =>
            {
                // 維持 UAC 原樣的 camelCase claim 名稱
                options.MapInboundClaims = false;
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuerSigningKey = true,
                    ValidateLifetime = validateLifetime,
                    RequireExpirationTime = requireExpirationTime,
                    ValidateAudience = validateAudience,
                    ValidateIssuer = validateIssuer,
                    ClockSkew = clockSkew
                };

                options.Events = new JwtBearerEvents
                {
                    OnTokenValidated = context =>
                    {
                        // 1. 若 ValidateLifetime 為 true，額外檢查 UAC 自訂的 expire claim（毫秒時間戳）
                        if (validateLifetime)
                        {
                            var expireClaim = context.Principal?.Claims.FirstOrDefault(c => string.Equals(c.Type, "expire", StringComparison.OrdinalIgnoreCase));
                            if (expireClaim != null)
                            {
                                if (!long.TryParse(expireClaim.Value?.Trim(), out var expireMs))
                                {
                                    context.Fail("Token contains invalid expire claim format.");
                                    return Task.CompletedTask;
                                }

                                var timeProvider = context.HttpContext.RequestServices.GetService<TimeProvider>() ?? TimeProvider.System;
                                var nowMs = timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
                                var clockSkewMs = (long)clockSkew.TotalMilliseconds;
                                if (nowMs - clockSkewMs > expireMs)
                                {
                                    context.Fail("Token has expired based on UAC expire claim.");
                                    return Task.CompletedTask;
                                }
                            }
                        }

                        // 2. JWT 通過驗證後，補上標準的 NameIdentifier 與 Name claim（來源為 empID、empName）
                        if (context.Principal?.Identity is ClaimsIdentity identity)
                        {
                            var empIdClaim = identity.Claims.FirstOrDefault(c => string.Equals(c.Type, "empID", StringComparison.OrdinalIgnoreCase));
                            if (empIdClaim != null && !string.IsNullOrWhiteSpace(empIdClaim.Value) && !identity.HasClaim(c => string.Equals(c.Type, ClaimTypes.NameIdentifier, StringComparison.OrdinalIgnoreCase)))
                            {
                                identity.AddClaim(new Claim(ClaimTypes.NameIdentifier, empIdClaim.Value.Trim()));
                            }

                            var empNameClaim = identity.Claims.FirstOrDefault(c => string.Equals(c.Type, "empName", StringComparison.OrdinalIgnoreCase));
                            if (empNameClaim != null && !string.IsNullOrWhiteSpace(empNameClaim.Value) && !identity.HasClaim(c => string.Equals(c.Type, ClaimTypes.Name, StringComparison.OrdinalIgnoreCase)))
                            {
                                identity.AddClaim(new Claim(ClaimTypes.Name, empNameClaim.Value.Trim()));
                            }

                            if (!identity.HasClaim(c => string.Equals(c.Type, "authMethod", StringComparison.OrdinalIgnoreCase)))
                            {
                                identity.AddClaim(new Claim("authMethod", "Jwt"));
                            }
                        }

                        return Task.CompletedTask;
                    }
                };
            })
            .AddScheme<ApiKeyAuthenticationOptions, ApiKeyAuthenticationHandler>(apiKeyScheme, options =>
            {
                options.SystemName = systemName;
                var cacheMinutes = configuration.GetValue<int?>("Authentication:Uac:ApiKeyCacheMinutes") ?? 30;
                options.ApiKeyCacheMinutes = cacheMinutes <= 0 ? 30 : cacheMinutes;
            });

            // 延遲配置 JwtBearerOptions 的 IssuerSigningKeyResolver，使用注入的 IJwksProvider
            services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
                .Configure<IJwksProvider>((options, jwksProvider) =>
                {
                    options.TokenValidationParameters.IssuerSigningKeyResolver = (token, securityToken, kid, validationParameters) =>
                    {
                        return jwksProvider.GetSigningKeys(kid);
                    };
                });

            // 授權配置：Authentication:RequireAuth 為 true 時設定 FallbackPolicy 要求已認證使用者
            services.AddAuthorization(options =>
            {
                if (requireAuth)
                {
                    options.FallbackPolicy = new AuthorizationPolicyBuilder()
                        .RequireAuthenticatedUser()
                        .Build();
                }
            });

            services.AddCors(options =>
            {
                options.AddPolicy("CorsPolicy",
                    builder => builder.AllowAnyOrigin()
                        .AllowAnyMethod()
                        .AllowAnyHeader());
            });

            // 補上 AddResponseCaching 以搭配 UseResponseCaching
            services.AddResponseCaching();

            services.AddSwaggerGen(c =>
            {
                c.SwaggerDoc("v1", new OpenApiInfo
                {
                    Version = "v1",
                    Title = "API Template " + Assembly.GetEntryAssembly()?.GetName().Version,
                    Description = "API Template",
                    Contact = new OpenApiContact
                    {
                        Name = "someone",
                        Email = "someone@innolux.com",
                    },
                    License = new OpenApiLicense
                    {
                        Name = "Innolux.com",
                    }
                });
                c.CustomOperationIds(apiDesc =>
                {
                    return apiDesc.TryGetMethodInfo(out MethodInfo methodInfo) ? methodInfo.Name : null;
                });
                var xmlFile = $"{Assembly.GetExecutingAssembly().GetName().Name}.xml";
                var xmlPath = Path.Combine(AppContext.BaseDirectory, xmlFile);
                if (File.Exists(xmlPath))
                {
                    c.IncludeXmlComments(xmlPath);
                }

                // Bearer (JWT) 安全定義
                c.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
                {
                    Description = "JWT Authorization header using the Bearer scheme. Enter your token in the text input below (without the 'Bearer ' prefix).",
                    Name = "Authorization",
                    In = ParameterLocation.Header,
                    Type = SecuritySchemeType.Http,
                    Scheme = "bearer",
                    BearerFormat = "JWT"
                });

                // X-API-Key 安全定義
                c.AddSecurityDefinition("X-API-Key", new OpenApiSecurityScheme
                {
                    Description = "API Key authentication using the X-API-Key header. Enter your API key below.",
                    Name = "X-API-Key",
                    In = ParameterLocation.Header,
                    Type = SecuritySchemeType.ApiKey
                });

                // 安全需求（Bearer 或 X-API-Key 二選一）
                c.AddSecurityRequirement(new OpenApiSecurityRequirement
                {
                    {
                        new OpenApiSecurityScheme
                        {
                            Reference = new OpenApiReference
                            {
                                Type = ReferenceType.SecurityScheme,
                                Id = "Bearer"
                            }
                        },
                        Array.Empty<string>()
                    }
                });

                c.AddSecurityRequirement(new OpenApiSecurityRequirement
                {
                    {
                        new OpenApiSecurityScheme
                        {
                            Reference = new OpenApiReference
                            {
                                Type = ReferenceType.SecurityScheme,
                                Id = "X-API-Key"
                            }
                        },
                        Array.Empty<string>()
                    }
                });
            });

            var checkBuilder = services.AddHealthChecks();
            checkBuilder.AddTypeActivatedCheck<HealthCheck>(
                "HealthCheck1",
                failureStatus: Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Degraded,
                tags: new[] { "Check1" });

            return services;
        }

        internal static bool IsThreePartJwt(string token)
        {
            if (string.IsNullOrWhiteSpace(token))
            {
                return false;
            }

            var span = token.AsSpan().Trim();
            int firstDot = span.IndexOf('.');
            if (firstDot <= 0)
            {
                return false;
            }

            var remainder1 = span.Slice(firstDot + 1);
            int secondDot = remainder1.IndexOf('.');
            if (secondDot <= 0)
            {
                return false;
            }

            var remainder2 = remainder1.Slice(secondDot + 1);
            if (remainder2.Length == 0)
            {
                return false;
            }

            int thirdDot = remainder2.IndexOf('.');
            return thirdDot == -1;
        }
    }
}
