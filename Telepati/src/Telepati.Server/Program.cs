using System.Text;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.FileProviders;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi;
using Telepati.Bot;
using Telepati.Domain;
using Telepati.Infrastructure;
using Telepati.Infrastructure.Services;
using Telepati.Server.Endpoints;
using Telepati.Server.Grpc;
using Telepati.Server.Realtime;
using Telepati.Shared.Configuration;

var builder = WebApplication.CreateBuilder(args);

var telepati = builder.Configuration.GetSection(TelepatiOptions.SectionName).Get<TelepatiOptions>() ?? new TelepatiOptions();

// --- services ---------------------------------------------------------------

builder.Services.AddTelepatiInfrastructure(builder.Configuration);
builder.Services.AddTelepatiBot();

builder.Services.AddSingleton<GrpcSubscriberRegistry>();
builder.Services.AddScoped<ChatOrchestrator>();

// Replaces the no-op notifier the infrastructure registers with one that can actually push.
builder.Services.AddSingleton<IRealtimeNotifier, CompositeRealtimeNotifier>();

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = telepati.Security.JwtIssuer,
            ValidAudience = telepati.Security.JwtAudience,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(telepati.Security.JwtSecret.PadRight(32, '!'))),
            ClockSkew = TimeSpan.FromMinutes(1)
        };

        options.Events = new JwtBearerEvents
        {
            OnMessageReceived = context =>
            {
                // Browsers cannot set headers on a WebSocket handshake, so SignalR passes the
                // token as a query string — accepted on the hub path only.
                var token = context.Request.Query["access_token"];
                if (!string.IsNullOrEmpty(token) && context.HttpContext.Request.Path.StartsWithSegments("/hubs"))
                {
                    context.Token = token;
                }
                return Task.CompletedTask;
            }
        };
    });

builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("AdminOnly", policy =>
        policy.RequireAssertion(context =>
            Enum.TryParse<UserRole>(context.User.FindFirst(System.Security.Claims.ClaimTypes.Role)?.Value, out var role)
            && role >= UserRole.Admin));
});

builder.Services.AddSignalR(options =>
{
    options.EnableDetailedErrors = builder.Environment.IsDevelopment();
    options.MaximumReceiveMessageSize = 1024 * 1024;
})
// MessagePack is offered first: measured ~20% smaller than JSON on a 50-message page, and it
// skips string parsing on both ends. JSON stays registered so a browser or tool that cannot
// negotiate MessagePack still connects.
.AddMessagePackProtocol()
.AddJsonProtocol(options =>
    options.PayloadSerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull);

// Chat payloads are JSON text and compress extremely well; enabling it for HTTPS is safe here
// because the API returns no attacker-controlled secrets alongside reflected input (BREACH).
builder.Services.AddResponseCompression(options =>
{
    options.EnableForHttps = true;
    options.MimeTypes = ["application/json", "application/grpc", "text/plain", "text/css", "application/javascript", "image/svg+xml"];
});

// The theme and client config are read on every app start but change rarely, so they are
// served from the output cache instead of hitting the database each time.
builder.Services.AddOutputCache(options =>
{
    options.AddPolicy("static-config", policy => policy.Expire(TimeSpan.FromSeconds(60)));
});

builder.Services.AddGrpc(options =>
{
    options.EnableDetailedErrors = builder.Environment.IsDevelopment();
    options.MaxReceiveMessageSize = 16 * 1024 * 1024;
});

builder.Services.Configure<FormOptions>(options => options.MultipartBodyLengthLimit = telepati.Limits.MaxUploadBytes);
builder.Services.Configure<Microsoft.AspNetCore.Server.Kestrel.Core.KestrelServerOptions>(options =>
    options.Limits.MaxRequestBodySize = telepati.Limits.MaxUploadBytes);

builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
    options.SerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
});

builder.Services.AddCors(options =>
    options.AddDefaultPolicy(policy =>
    {
        if (telepati.Security.AllowedCorsOrigins.Count > 0)
        {
            policy.WithOrigins([.. telepati.Security.AllowedCorsOrigins])
                  .AllowAnyHeader().AllowAnyMethod().AllowCredentials();
        }
        else
        {
            // An empty list means development: accept whatever origin asks, credentials included.
            policy.SetIsOriginAllowed(_ => true).AllowAnyHeader().AllowAnyMethod().AllowCredentials();
        }
    }));

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = $"{telepati.Branding.AppName} API",
        Version = "v1",
        Description = $"REST API {telepati.Branding.AppName} — {telepati.Branding.Tagline}\n\n" +
                      $"Dibuat oleh {telepati.Branding.Company}, dipimpin oleh {telepati.Branding.Leader}.\n\n" +
                      "Klien bisa memilih transport: SignalR (`/hubs/telepati`), gRPC, atau REST di bawah ini.",
        Contact = new OpenApiContact { Name = telepati.Branding.Company, Email = telepati.Branding.SupportEmail }
    });

    options.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Name = "Authorization",
        Type = SecuritySchemeType.Http,
        Scheme = "bearer",
        BearerFormat = "JWT",
        In = ParameterLocation.Header,
        Description = "Masukkan token dari /api/auth/login."
    });

    options.AddSecurityRequirement(document => new OpenApiSecurityRequirement
    {
        [new OpenApiSecuritySchemeReference("Bearer", document)] = []
    });
});

var app = builder.Build();

// --- pipeline ---------------------------------------------------------------

await app.Services.InitializeTelepatiDatabaseAsync();

if (app.Environment.IsDevelopment())
{
    app.UseDeveloperExceptionPage();
}

// Compression sits before the endpoints so their responses pass through it.
app.UseResponseCompression();

app.UseCors();
app.UseAuthentication();
app.UseAuthorization();
app.UseOutputCache();

app.UseSwagger();
app.UseSwaggerUI(options =>
{
    options.SwaggerEndpoint("/swagger/v1/swagger.json", $"{telepati.Branding.AppName} API v1");
    options.DocumentTitle = $"{telepati.Branding.AppName} API";
});

// Local-disk storage is served directly; cloud providers hand out their own signed URLs.
if (telepati.Storage.Provider.Equals("FileSystem", StringComparison.OrdinalIgnoreCase))
{
    var storageRoot = Path.IsPathRooted(telepati.Storage.RootPath)
        ? telepati.Storage.RootPath
        : Path.Combine(AppContext.BaseDirectory, telepati.Storage.RootPath);

    Directory.CreateDirectory(storageRoot);
    app.UseStaticFiles(new StaticFileOptions
    {
        FileProvider = new PhysicalFileProvider(storageRoot),
        RequestPath = telepati.Storage.PublicBaseUrl.TrimEnd('/')
    });
}

app.MapAuthEndpoints();
app.MapUserEndpoints();
app.MapContactEndpoints();
app.MapChatEndpoints();
app.MapMessageEndpoints();
app.MapMediaEndpoints();
app.MapSocialEndpoints();
app.MapAdminEndpoints();
app.MapPublicEndpoints();

app.MapHub<TelepatiHub>("/hubs/telepati");

if (telepati.Features.EnableGrpcTransport)
{
    app.MapGrpcService<TelepatiGrpcService>();
}

app.MapGet("/", () => Results.Redirect("/swagger"));

app.Run();

/// <summary>Exposed so integration tests can spin up the real server.</summary>
public partial class Program;
