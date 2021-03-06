﻿using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Serialization;
using StackExchange.Redis;
using System;
using System.Linq;
using Microsoft.WindowsAzure.Storage;
using Wukong.Helpers;
using Wukong.Options;
using Wukong.Repositories;
using Wukong.Services;

namespace Wukong
{
    public class Startup
    {
        private IConfigurationRoot Configuration { get; }
        private readonly SettingOptions settings = new SettingOptions();

        public Startup(IHostingEnvironment env)
        {
            var builder = new ConfigurationBuilder()
                .SetBasePath(env.ContentRootPath)
                .AddJsonFile("appsettings.json", true, true)
                .AddJsonFile("runtime/appsettings.json", true)
                .AddJsonFile($"appsettings.{env.EnvironmentName}.json", true);

            if (env.IsEnvironment("Development"))
            {
                // This will push telemetry data through Application Insights pipeline faster, allowing you to view results immediately.
                builder.AddApplicationInsightsSettings(developerMode: true);

                builder.AddUserSecrets<ProviderOptions>();
                builder.AddUserSecrets<ApplicationInsightsOptions>();
                builder.AddUserSecrets<AzureAdB2COptions>();
                builder.AddUserSecrets<AzureAdB2CPolicies>();
            }

            builder.AddEnvironmentVariables();
            if (env.IsDevelopment())
            {
                builder.AddApplicationInsightsSettings(developerMode: true);
            }
            Configuration = builder.Build();
        }

        // This method gets called by the runtime. Use this method to add services to the container.
        public void ConfigureServices(IServiceCollection services)
        {
            services.Configure<SettingOptions>(Configuration);
            Configuration.Bind(settings);

            // Add framework services.
            services.AddApplicationInsightsTelemetry(Configuration);

            // Use redis to store data protection key and session if necessary.
            if (!String.IsNullOrEmpty(settings.RedisConnectionString))
            {
                string redisConnectionString = RedisConnectionUtil.RedisConnectionDnsLookup(settings.RedisConnectionString);

                var redis = ConnectionMultiplexer.Connect(redisConnectionString);
                services.AddDataProtection().PersistKeysToRedis(redis, "DataProtection-Keys");
                services.AddDistributedRedisCache(option =>
                {
                    // Workaround.
                    option.Configuration = redisConnectionString;
                });
            }
            else
            {
                services.AddDataProtection();
                services.AddDistributedMemoryCache();
            }
            services.AddSession();

            services.AddOptions();
            services.AddCors();

            var store = CloudStorageAccount.Parse(settings.AzureStorageConnectionString);

            services.AddScoped<IUserConfigurationRepository, UserConfigurationRepository>();
            // Dependency injection.
            services.AddSingleton(store);
            services.AddSingleton<IUserManager, UserManager>();
            services.AddSingleton<ISocketManager, Services.SocketManager>();
            services.AddSingleton<IProvider, Provider>();
            services.AddSingleton<IChannelManager, ChannelManager>();
            services.AddSingleton<IStorage, Storage>();
            services.AddScoped<IUserService, UserService>();


            services.AddAuthentication(options =>
            {
                options.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
            })
            .AddCookie(AuthenticationOptions.CookieAuthenticationOptions(settings.RedisConnectionString))
            .AddOpenIdConnect(AzureOpenIdConnectionOptions.Options(settings.AzureAdB2COptions, settings.AzureAdB2CPolicies.WebSignin));
            services.AddMvc()
                .AddJsonOptions(opt =>
                {
                    opt.SerializerSettings.ContractResolver = new CamelCasePropertyNamesContractResolver();
                });
        }

        // This method gets called by the runtime. Use this method to configure the HTTP request pipeline.
        public void Configure(IApplicationBuilder app, IHostingEnvironment env, ILoggerFactory loggerFactory)
        {

            loggerFactory.AddConsole(Configuration.GetSection("Logging"));
            loggerFactory.AddDebug();

            app.UseSession();
            app.UseForwardedHeaders(new ForwardedHeadersOptions
            {
                ForwardedHeaders = Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedProto
            });

            app.UseCors(builder => builder.WithOrigins(settings.WukongOrigins).WithOrigins("http://127.0.0.1:8080", "http://localhost:8080")
                .AllowAnyMethod().AllowAnyHeader().AllowCredentials());

            app.UseAuthentication();
            app.UseWebSockets();
            app.UseMiddleware<UserManagerMiddleware>();
            app.UseMiddleware<SocketManagerMiddleware>();

            app.UseMvc();
        }
    }
}
