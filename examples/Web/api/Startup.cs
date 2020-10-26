﻿namespace WebAPI
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Net;
    using System.Reflection;
    using System.Text.Json;
    using System.Text.Json.Serialization;
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft.AspNetCore.Authentication.JwtBearer;
    using Microsoft.AspNetCore.Builder;
    using Microsoft.AspNetCore.Hosting;
    using Microsoft.AspNetCore.Mvc;
    using Microsoft.AspNetCore.Mvc.ApiExplorer;
    using Microsoft.Extensions.Caching.Memory;
    using Microsoft.Extensions.Configuration;
    using Microsoft.Extensions.DependencyInjection;
    using Microsoft.Extensions.FileProviders;
    using Microsoft.Extensions.Hosting;
    using Microsoft.IdentityModel.Tokens;
    using Microsoft.OpenApi.Models;
    using Soulseek;
    using Soulseek.Diagnostics;
    using WebAPI.Entities;
    using WebAPI.Security;
    using WebAPI.Trackers;

    public class Startup
    {
        internal static string Username { get; set; }
        internal static string Password { get; set; }
        internal static string BasePath { get; set; }
        internal static string WebRoot { get; set; }
        internal static int ListenPort { get; set; }
        internal static string OutputDirectory { get; set; }
        internal static string SharedDirectory { get; set; }
        internal static long SharedCacheTTL { get; set; }
        internal static bool EnableDistributedNetwork { get; set; }
        internal static int DistributedChildLimit { get; set; }
        internal static DiagnosticLevel DiagnosticLevel { get; set; }
        internal static int ConnectTimeout { get; set; }
        internal static int InactivityTimeout { get; set; }
        internal static bool EnableSecurity { get; set; }
        internal static int TokenTTL { get; set; }
        internal static int RoomMessageLimit { get; set; }

        internal static SymmetricSecurityKey JwtSigningKey { get; set; }

        private SoulseekClient Client { get; set; }
        private object ConsoleSyncRoot { get; } = new object();
        private ISharedFileCache SharedFileCache { get; set; }

        public Startup(IConfiguration configuration)
        {
            Configuration = configuration;

            Username = Configuration.GetValue<string>("USERNAME");
            Password = Configuration.GetValue<string>("PASSWORD");
            BasePath = Configuration.GetValue<string>("BASE_PATH");
            WebRoot = Configuration.GetValue<string>("WEB_ROOT");
            ListenPort = Configuration.GetValue<int>("LISTEN_PORT", 50000);
            OutputDirectory = Configuration.GetValue<string>("OUTPUT_DIR");
            SharedDirectory = Configuration.GetValue<string>("SHARED_DIR");
            SharedCacheTTL = Configuration.GetValue<long>("SHARED_CACHE_TTL", 900000); // 15 minutes
            EnableDistributedNetwork = Configuration.GetValue<bool>("ENABLE_DNET", true);
            DistributedChildLimit = Configuration.GetValue<int>("DNET_CHILD_LIMIT", 10);
            DiagnosticLevel = Configuration.GetValue<DiagnosticLevel>("DIAGNOSTIC", DiagnosticLevel.Info);
            ConnectTimeout = Configuration.GetValue<int>("CONNECT_TIMEOUT", 5000);
            InactivityTimeout = Configuration.GetValue<int>("INACTIVITY_TIMEOUT", 15000);
            EnableSecurity = Configuration.GetValue<bool>("ENABLE_SECURITY", true);
            TokenTTL = Configuration.GetValue<int>("TOKEN_TTL", 86400000); // 24 hours
            RoomMessageLimit = Configuration.GetValue<int>("ROOM_MESSAGE_LIMIT", 100);

            JwtSigningKey = new SymmetricSecurityKey(PBKDF2.GetKey(Password));

            SharedFileCache = new SharedFileCache(SharedDirectory, SharedCacheTTL);
        }

        public IConfiguration Configuration { get; }

        public void ConfigureServices(IServiceCollection services)
        {
            services.AddCors(options => options.AddPolicy("AllowAll", builder => builder.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader()));

            if (EnableSecurity)
            {
                services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
                    .AddJwtBearer(options =>
                    {
                        options.TokenValidationParameters = new TokenValidationParameters
                        {
                            ClockSkew = TimeSpan.FromMinutes(5),
                            RequireSignedTokens = true,
                            RequireExpirationTime = true,
                            ValidateLifetime = true,
                            ValidIssuer = "slsk-web-example",
                            ValidateIssuer = true,
                            ValidateAudience = false,
                            IssuerSigningKey = JwtSigningKey,
                            ValidateIssuerSigningKey = true,
                        };
                    });
            }
            else
            {
                services.AddAuthentication(PassthroughAuthentication.AuthenticationScheme)
                    .AddScheme<PassthroughAuthenticationOptions, PassthroughAuthenticationHandler>(PassthroughAuthentication.AuthenticationScheme, options =>
                    {
                        options.Username = Username;
                    });
            }

            services.AddMvc(options =>
            {
                options.EnableEndpointRouting = false;
            })
                .SetCompatibilityVersion(CompatibilityVersion.Latest)
                .AddJsonOptions(options =>
                {
                    options.JsonSerializerOptions.Converters.Add(new IPAddressConverter());
                    options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());
                    options.JsonSerializerOptions.IgnoreNullValues = true;
                });

            services.AddRouting(options => options.LowercaseUrls = true);

            services.AddApiVersioning(options => options.ReportApiVersions = true);
            services.AddVersionedApiExplorer(options =>
            {
                options.GroupNameFormat = "'v'VVV";
                options.SubstituteApiVersionInUrl = true;
            });

            services.AddSwaggerGen(options =>
            {
                options.DescribeAllParametersInCamelCase();
                options.SwaggerDoc("v1",
                    new OpenApiInfo
                    {
                        Title = "Soulseek.NET Example API",
                        Version = "v1"
                    }
                 );

                options.IncludeXmlComments(Path.Combine(AppContext.BaseDirectory, typeof(Startup).GetTypeInfo().Assembly.GetName().Name + ".xml"));
            });

            services.AddSingleton<ISoulseekClient, SoulseekClient>(serviceProvider => Client);
            services.AddSingleton<ITransferTracker, TransferTracker>();
            services.AddSingleton<ISearchTracker, SearchTracker>();
            services.AddSingleton<IBrowseTracker, BrowseTracker>();
            services.AddSingleton<IConversationTracker, ConversationTracker>();
            services.AddSingleton<IRoomTracker, RoomTracker>(_ => new RoomTracker(messageLimit: RoomMessageLimit));
        }

        public void Configure(
            IApplicationBuilder app, 
            IWebHostEnvironment env, 
            IApiVersionDescriptionProvider provider, 
            ITransferTracker tracker, 
            IBrowseTracker browseTracker, 
            IConversationTracker conversationTracker,
            IRoomTracker roomTracker)
        {
            if (!env.IsDevelopment())
            {
                app.UseHsts();
            }

            app.UseCors("AllowAll");

            BasePath = BasePath ?? "/";
            BasePath = BasePath.StartsWith("/") ? BasePath : $"/{BasePath}";

            app.UsePathBase(BasePath);

            // remove any errant double forward slashes which may have been introduced
            // by a reverse proxy or having the base path removed
            app.Use(async (context, next) => {
                context.Request.Path = context.Request.Path.ToString().Replace("//", "/");
                await next();
            });

            WebRoot = WebRoot ?? Path.Combine(Path.GetDirectoryName(new Uri(Assembly.GetExecutingAssembly().GetName().CodeBase).AbsolutePath), "wwwroot");
            Console.WriteLine($"Serving static content from {WebRoot}");

            var fileServerOptions = new FileServerOptions
            {
                FileProvider = new PhysicalFileProvider(WebRoot),
                RequestPath = "",
                EnableDirectoryBrowsing = false,
                EnableDefaultFiles = true
            };

            app.UseFileServer(fileServerOptions);

            app.UseAuthentication();
            app.UseMvc();

            app.UseSwagger();
            app.UseSwaggerUI(options => provider.ApiVersionDescriptions.ToList()
                .ForEach(description => options.SwaggerEndpoint($"/swagger/{description.GroupName}/swagger.json", description.GroupName)));

            // if we made it this far and the route still wasn't matched, return the index
            // this is required so that SPA routing (React Router, etc) can work properly
            app.Use(async (context, next) =>
            {
                // exclude API routes which are not matched or return a 404
                if (!context.Request.Path.StartsWithSegments("/api"))
                {
                    context.Request.Path = "/";
                }

                await next();
            });

            app.UseFileServer(fileServerOptions);

            // ---------------------------------------------------------------------------------------------------------------------------------------------
            // begin SoulseekClient implementation
            // ---------------------------------------------------------------------------------------------------------------------------------------------

            // create options for the client.
            // see the implementation of Func<> and Action<> options for detailed info.
            var clientOptions = new SoulseekClientOptions(
                listenPort: ListenPort,
                userEndPointCache: new UserEndPointCache(),
                distributedChildLimit: DistributedChildLimit,
                enableDistributedNetwork: EnableDistributedNetwork,
                minimumDiagnosticLevel: DiagnosticLevel,
                autoAcknowledgePrivateMessages: false,
                serverConnectionOptions: new ConnectionOptions(connectTimeout: ConnectTimeout, inactivityTimeout: InactivityTimeout),
                peerConnectionOptions: new ConnectionOptions(connectTimeout: ConnectTimeout, inactivityTimeout: InactivityTimeout),
                transferConnectionOptions: new ConnectionOptions(connectTimeout: ConnectTimeout, inactivityTimeout: InactivityTimeout),
                userInfoResponseResolver: UserInfoResponseResolver,
                browseResponseResolver: BrowseResponseResolver,
                directoryContentsResponseResolver: DirectoryContentsResponseResolver,
                enqueueDownloadAction: (username, endpoint, filename) => EnqueueDownloadAction(username, endpoint, filename, tracker),
                searchResponseResolver: SearchResponseResolver);

            Client = new SoulseekClient(options: clientOptions);

            // bind the DiagnosticGenerated event so we can trap and display diagnostic messages.  this is optional, and if the event 
            // isn't bound the minimumDiagnosticLevel should be set to None.
            Client.DiagnosticGenerated += (e, args) =>
            {
                lock (ConsoleSyncRoot)
                {
                    if (args.Level == DiagnosticLevel.Debug) Console.ForegroundColor = ConsoleColor.DarkGray;
                    if (args.Level == DiagnosticLevel.Warning) Console.ForegroundColor = ConsoleColor.Yellow;

                    Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [DIAGNOSTIC:{e.GetType().Name}] [{args.Level}] {args.Message}");
                    Console.ResetColor();
                }
            };

            // bind transfer events.  see TransferStateChangedEventArgs and TransferProgressEventArgs.
            Client.TransferStateChanged += (e, args) =>
                Console.WriteLine($"[{args.Transfer.Direction.ToString().ToUpper()}] [{args.Transfer.Username}/{Path.GetFileName(args.Transfer.Filename)}] {args.PreviousState} => {args.Transfer.State}{(args.Transfer.State.HasFlag(TransferStates.Completed) ? $" ({args.Transfer.BytesTransferred}/{args.Transfer.Size} = {args.Transfer.PercentComplete}%)" : string.Empty)}");
            Client.TransferProgressUpdated += (e, args) =>
            {
                // this is really verbose.
                // Console.WriteLine($"[{args.Transfer.Direction.ToString().ToUpper()}] [{args.Transfer.Username}/{Path.GetFileName(args.Transfer.Filename)}] {args.Transfer.BytesTransferred}/{args.Transfer.Size} {args.Transfer.PercentComplete}% {args.Transfer.AverageSpeed}kb/s");
            };

            // bind BrowseProgressUpdated to track progress of browse response payload transfers.  
            // these can take a while depending on number of files shared.
            Client.BrowseProgressUpdated += (e, args) =>
            {
                browseTracker.AddOrUpdate(args.Username, args);
            };

            // bind UserStatusChanged to monitor the status of users added via AddUserAsync().
            Client.UserStatusChanged += (e, args) =>
            {
                // Console.WriteLine($"[USER] {args.Username}: {args.Status}");
            };

            Client.PrivateMessageReceived += (e, args) =>
            {
                conversationTracker.AddOrUpdate(args.Username, PrivateMessage.FromEventArgs(args));
            };

            Client.RoomMessageReceived += (e, args) =>
            {
                var message = RoomMessage.FromEventArgs(args, DateTime.UtcNow);
                roomTracker.AddOrUpdateMessage(args.RoomName, message);
            };

            Client.RoomJoined += (e, args) =>
            {
                if (args.Username != Username) // this will fire when we join a room; track that through the join operation.
                {
                    roomTracker.TryAddUser(args.RoomName, args.UserData);
                }
            };

            Client.RoomLeft += (e, args) =>
            {
                roomTracker.TryRemoveUser(args.RoomName, args.Username);
            };

            Client.Disconnected += async (e, args) =>
            {
                Console.WriteLine($"Disconnected from Soulseek server: {args.Message}");

                // don't reconnect if the disconnecting Exception is either of these types.
                // if KickedFromServerException, another client was most likely signed in, and retrying will cause a connect loop.
                // if ObjectDisposedException, the client is shutting down.
                if (!(args.Exception is KickedFromServerException || args.Exception is ObjectDisposedException))
                {
                    Console.WriteLine($"Attepting to reconnect...");
                    await Client.ConnectAsync(Username, Password);
                }
            };

            Task.Run(async () =>
            {
                await Client.ConnectAsync(Username, Password);
            }).GetAwaiter().GetResult();

            Console.WriteLine($"Connected and logged in.");
        }

        /// <summary>
        ///     Creates and returns a <see cref="UserInfo"/> object in response to a remote request.
        /// </summary>
        /// <param name="username">The username of the requesting user.</param>
        /// <param name="endpoint">The IP endpoint of the requesting user.</param>
        /// <returns>A Task resolving the UserInfo instance.</returns>
        private Task<UserInfo> UserInfoResponseResolver(string username, IPEndPoint endpoint)
        {
            var info = new UserInfo(
                description: $"Soulseek.NET Web Example! also, your username is {username}, and IP endpoint is {endpoint}",
                picture: System.IO.File.ReadAllBytes(@"slsk_bird.jpg"),
                uploadSlots: 1,
                queueLength: 0,
                hasFreeUploadSlot: false);

            return Task.FromResult(info);
        }

        /// <summary>
        ///     Creates and returns an <see cref="IEnumerable{T}"/> of <see cref="Soulseek.Directory"/> in response to a remote request.
        /// </summary>
        /// <param name="username">The username of the requesting user.</param>
        /// <param name="endpoint">The IP endpoint of the requesting user.</param>
        /// <returns>A Task resolving an IEnumerable of Soulseek.Directory.</returns>
        private Task<BrowseResponse> BrowseResponseResolver(string username, IPEndPoint endpoint)
        {
            var directories = System.IO.Directory
                .GetDirectories(SharedDirectory, "*", SearchOption.AllDirectories)
                .Select(dir => new Soulseek.Directory(dir, System.IO.Directory.GetFiles(dir)
                    .Select(f => new Soulseek.File(1, Path.GetFileName(f), new FileInfo(f).Length, Path.GetExtension(f), 0))));

            return Task.FromResult(new BrowseResponse(directories));
        }

        /// <summary>
        ///     Creates and returns a <see cref="Soulseek.Directory"/> in response to a remote request.
        /// </summary>
        /// <param name="username">The username of the requesting user.</param>
        /// <param name="endpoint">The IP endpoint of the requesting user.</param>
        /// <param name="token">The unique token for the request, supplied by the requesting user.</param>
        /// <param name="directory">The requested directory.</param>
        /// <returns>A Task resolving an instance of Soulseek.Directory containing the contents of the requested directory.</returns>
        private Task<Soulseek.Directory> DirectoryContentsResponseResolver(string username, IPEndPoint endpoint, int token, string directory)
        {
            var result = new Soulseek.Directory(directory, System.IO.Directory.GetFiles(directory)
                    .Select(f => new Soulseek.File(1, Path.GetFileName(f), new FileInfo(f).Length, Path.GetExtension(f), 0)));

            return Task.FromResult(result);
        }

        /// <summary>
        ///     Invoked upon a remote request to download a file.  
        /// </summary>
        /// <param name="username">The username of the requesting user.</param>
        /// <param name="endpoint">The IP endpoint of the requesting user.</param>
        /// <param name="filename">The filename of the requested file.</param>
        /// <param name="tracker">(for example purposes) the ITransferTracker used to track progress.</param>
        /// <returns>A Task representing the asynchronous operation.</returns>
        /// <exception cref="DownloadEnqueueException">Thrown when the download is rejected.  The Exception message will be passed to the remote user.</exception>
        /// <exception cref="Exception">Thrown on any other Exception other than a rejection.  A generic message will be passed to the remote user for security reasons.</exception>
        private Task EnqueueDownloadAction(string username, IPEndPoint endpoint, string filename, ITransferTracker tracker)
        {
            _ = endpoint;
            filename = filename.ToLocalOSPath();
            var fileInfo = new FileInfo(filename);

            if (!fileInfo.Exists)
            {
                Console.WriteLine($"[UPLOAD REJECTED] File {filename} not found.");
                throw new DownloadEnqueueException($"File not found.");
            }

            if (tracker.TryGet(TransferDirection.Upload, username, filename, out _))
            {
                // in this case, a re-requested file is a no-op.  normally we'd want to respond with a 
                // PlaceInQueueResponse
                Console.WriteLine($"[UPLOAD RE-REQUESTED] [{username}/{filename}]");
                return Task.CompletedTask;
            }

            // create a new cancellation token source so that we can cancel the upload from the UI.
            var cts = new CancellationTokenSource();
            var topts = new TransferOptions(stateChanged: (e) => tracker.AddOrUpdate(e, cts), progressUpdated: (e) => tracker.AddOrUpdate(e, cts), governor: (t, c) => Task.Delay(1));

            // accept all download requests, and begin the upload immediately.
            // normally there would be an internal queue, and uploads would be handled separately.
            Task.Run(async () =>
            {
                using (var stream = new FileStream(fileInfo.FullName, FileMode.Open, FileAccess.Read))
                {
                    await Client.UploadAsync(username, fileInfo.FullName, fileInfo.Length, stream, options: topts, cancellationToken: cts.Token);
                }
            }).ContinueWith(t =>
            {
                Console.WriteLine($"[UPLOAD FAILED] {t.Exception}");
            }, TaskContinuationOptions.NotOnRanToCompletion); // fire and forget

            // return a completed task so that the invoking code can respond to the remote client.
            return Task.CompletedTask;
        }

        /// <summary>
        ///     Creates and returns a <see cref="SearchResponse"/> in response to the given <paramref name="query"/>.
        /// </summary>
        /// <param name="username">The username of the requesting user.</param>
        /// <param name="token">The search token.</param>
        /// <param name="query">The search query.</param>
        /// <returns>A Task resolving a SearchResponse, or null.</returns>
        private Task<SearchResponse> SearchResponseResolver(string username, int token, SearchQuery query)
        {
            var defaultResponse = Task.FromResult<SearchResponse>(null);

            // some bots continually query for very common strings.  blacklist known names here.
            var blacklist = new[] { "Lola45", "Lolo51", "rajah" };
            if (blacklist.Contains(username))
            {
                return defaultResponse;
            }

            // some bots and perhaps users search for very short terms.  only respond to queries >= 3 characters.  sorry, U2 fans.
            if (query.Query.Length < 3)
            {
                return defaultResponse;
            }

            var results = SharedFileCache.Search(query);

            if (results.Count() > 0)
            {
                Console.WriteLine($"[SENDING SEARCH RESULTS]: {results.Count()} records to {username} for query {query.SearchText}");

                return Task.FromResult(new SearchResponse(
                    Username,
                    token,
                    freeUploadSlots: 1,
                    uploadSpeed: 0,
                    queueLength: 0,
                    fileList: results));
            }

            // if no results, either return null or an instance of SearchResponse with a fileList of length 0
            // in either case, no response will be sent to the requestor.
            return Task.FromResult<SearchResponse>(null);
        }

        class IPAddressConverter : JsonConverter<IPAddress>
        {
            public override bool CanConvert(Type objectType) => (objectType == typeof(IPAddress));

            public override IPAddress Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => IPAddress.Parse(reader.GetString());

            public override void Write(Utf8JsonWriter writer, IPAddress value, JsonSerializerOptions options) => writer.WriteStringValue(value.ToString());
        }

        class UserEndPointCache : IUserEndPointCache
        {
            public UserEndPointCache()
            {
                Cache = new MemoryCache(new MemoryCacheOptions());
            }

            private IMemoryCache Cache { get; }

            public void AddOrUpdate(string username, IPEndPoint endPoint)
            {
                Cache.Set(username, endPoint, TimeSpan.FromSeconds(60));
            }

            public bool TryGet(string username, out IPEndPoint endPoint)
            {
                return Cache.TryGetValue(username, out endPoint);
            }
        }
    }
}