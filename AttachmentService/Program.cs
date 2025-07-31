using AttachmentService.Clients;
using AttachmentService.Data;
using AttachmentService.Handler;
using AttachmentService.Interfaces;
using AttachmentService.Services;
using RemaxApi.Shared.Authentication.Extensions;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Any;
using Microsoft.OpenApi.Interfaces;
using Microsoft.OpenApi.Models;
using System.Security.Claims;
using System.Text;
using System.Text.Json.Serialization;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowSpecificOrigin",
        builder => builder.WithOrigins("http://localhost")
                          .AllowAnyHeader()
                          .AllowAnyMethod());
});

var useMockOAuth = builder.Configuration.GetValue<bool>("Authentication:UseMockOAuth");
var jwtValidationSecretKey = builder.Configuration["JwtSettings:SecretKey"];
var signingKeyId = builder.Configuration["JwtSettings:SigningKeyId"];

if (string.IsNullOrEmpty(jwtValidationSecretKey) || string.IsNullOrEmpty(signingKeyId))
{
    throw new InvalidOperationException("JWT Secret not found in configuration.");
}

// Services
builder.Services.AddTransient<IAttachmentFactoryService, AttachmentFactoryService>();
builder.Services.AddScoped<UserClaimService>();
builder.Services.AddScoped<IEntityPropertyPatchService, EntityPropertyPatchService>();
builder.Services.AddScoped<IAttachmentPatchService, AttachmentPatchService>();

// Registra HttpContextAccessor necessario per i servizi JWT
builder.Services.AddHttpContextAccessor();

// Registra i servizi JWT condivisi
builder.Services.AddExternalJwtAuthentication();
builder.Services.AddHttpClient<IMappingServiceHttpClient, MappingServiceHttpClient>(client =>
{
    client.BaseAddress = new Uri(builder.Configuration["MappingService:BaseUrl"]!);
})
.AddHttpMessageHandler(sp =>
{
    var httpContextAccessor = sp.GetRequiredService<IHttpContextAccessor>();
    return new AuthTokenHandler(httpContextAccessor);
});

// Controllers
builder.Services.AddControllers()
    .AddJsonOptions(options => {
        options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());
    });

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "Attachment Service API",
        Version = "v1"
    });

    var useMockOAuth = builder.Configuration.GetValue<bool>("Authentication:UseMockOAuth");


    string? authorizationUrl;
    string? tokenUrl;
    string? swaggerClientId;
    string[]? swaggerScopes;

    if (useMockOAuth)
    {
        swaggerClientId = builder.Configuration["MockOAuthSettings:SwaggerClientId"];
        swaggerScopes = builder.Configuration.GetSection("MockOAuthSettings:SwaggerScopes").Get<string[]>();
        authorizationUrl = builder.Configuration["MockOAuthSettings:AuthorizationUrl"];
        tokenUrl = builder.Configuration["MockOAuthSettings:TokenUrl"];
    }
    else
    {
        swaggerClientId = builder.Configuration["OAuthSettings:SwaggerClientId"];
        swaggerScopes = builder.Configuration.GetSection("OAuthSettings:SwaggerScopes").Get<string[]>();
        authorizationUrl = builder.Configuration["OAuthSettings:AuthorizationUrl"];
        tokenUrl = builder.Configuration["OAuthSettings:TokenUrl"];
    }

    if (string.IsNullOrEmpty(authorizationUrl) || string.IsNullOrEmpty(tokenUrl))
    {
        throw new InvalidOperationException("OAuth AuthorizationUrl or TokenEndpoint not configured for Swagger.");
    }

    // JWT Bearer Token configuration for Swagger
    options.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Description = "JWT Authorization header usando Bearer scheme. Esempio: \"Authorization: Bearer {token}\"",
        Name = "Authorization",
        In = ParameterLocation.Header,
        Type = SecuritySchemeType.ApiKey,
        Scheme = "Bearer",
        BearerFormat = "JWT"
    });

    options.AddSecurityRequirement(new OpenApiSecurityRequirement
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
            new string[] {}
        }
    });
});

// --- Configurazione Servizi di Autenticazione ---
builder.Services.AddAuthentication(options =>
{
    options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
    options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
})
.AddJwtBearer(JwtBearerDefaults.AuthenticationScheme, options =>
{
    options.Events = new JwtBearerEvents
    {
        OnTokenValidated = context =>
        {
            Console.WriteLine($"Token Validated for user: {context.Principal?.Identity?.Name}");
            foreach (var claim in context.Principal?.Claims ?? Enumerable.Empty<Claim>())
            {
                Console.WriteLine($"  Claim: {claim.Type} = {claim.Value}");
            }
            return Task.CompletedTask;
        },
        OnAuthenticationFailed = context =>
        {
            Console.WriteLine($"Authentication failed: {context.Exception.Message}");
            return Task.FromException(context.Exception);
        },
        OnChallenge = context =>
        {
            Console.WriteLine($"OnChallenge called. Reason: {context.AuthenticateFailure?.Message ?? "None"}");
            return Task.CompletedTask;
        },
        OnForbidden = context =>
        {
            Console.WriteLine($"OnForbidden called. User: {context.Principal?.Identity?.Name}");
            return Task.CompletedTask;
        }
    };

    if (useMockOAuth)
    {
        options.RequireHttpsMetadata = false;
        options.SaveToken = true;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = "http://localhost:7005",
            ValidateAudience = true,
            ValidAudience = "api1",
            ValidateLifetime = true,
            ClockSkew = TimeSpan.Zero,

            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.ASCII.GetBytes(jwtValidationSecretKey))
            {
                KeyId = signingKeyId
            }
        };
    }
    else
    {
        options.Authority = builder.Configuration["OAuthSettings:Authority"];
        options.Audience = builder.Configuration["OAuthSettings:Audience"];
        options.RequireHttpsMetadata = builder.Environment.IsProduction();
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true
        };
    }
});

// --- Configurazione Servizi di Autorizzazione ---
builder.Services.AddAuthorization(options =>
{
    options.FallbackPolicy = new AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .Build();
});

builder.Services.AddDbContext<AttachmentDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("DefaultConnection")));


var app = builder.Build();


if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI(options =>
    {
        options.SwaggerEndpoint("/swagger/v1/swagger.json", "AttachmentService API v1");
        options.DocumentTitle = "AttachmentService API - Swagger UI";
    });
}

using (var scope = app.Services.CreateScope())
{
    var services = scope.ServiceProvider;
    try
    {
        var dbContext = services.GetRequiredService<AttachmentDbContext>();
        dbContext.Database.Migrate();
        Console.WriteLine("Database migration applied successfully.");
    }
    catch (Exception ex)
    {
        var logger = services.GetRequiredService<ILogger<Program>>();
        logger.LogError(ex, "An error occurred while migrating the database.");
    }
}

app.UseRouting();

// Usa il middleware JWT condiviso
app.UseExternalJwtValidation();
app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();

app.Run();