module BuildStats.App

open System
open System.IO
open System.Net.Http
open System.Threading.Tasks
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Hosting
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Logging
open FSharp.Control.Tasks.V2.ContextInsensitive
open Giraffe
open Giraffe.GiraffeViewEngine
open BuildStats.Common
open BuildStats.PackageServices
open BuildStats.BuildHistoryCharts
open BuildStats.Models

// ---------------------------------
// Web app
// ---------------------------------

let devApiSecret = Guid.NewGuid().ToString("n").Substring(0, 10)

let apiSecret =
        Environment.GetEnvironmentVariable "API_SECRET"
        |> Str.toOption
        |> function
            | Some v -> v
            | None   -> devApiSecret

let accessForbidden =
    RequestErrors.FORBIDDEN
        "Access forbidden. Please provide a valid API secret in order to access this resource."

let finish = Some >> Task.FromResult

let requiresApiSecret =
    fun (next : HttpFunc) (ctx : HttpContext) ->
        (match ctx.TryGetQueryStringValue "apiSecret" with
        | Some secretFromQuery ->
            match apiSecret.Equals secretFromQuery with
            | true  -> next
            | false -> accessForbidden finish
        | None      -> accessForbidden finish) ctx


let cssHandler (bundle : string) =
    setHttpHeader "Content-Type" "text/css"
    >=> setHttpHeader "Cache-Control" "public, max-age=31536000"
    >=> setHttpHeader "ETag" Views.cssHash
    >=> setBodyFromString bundle

let svg (body : string) =
    setHttpHeader "Content-Type" "image/svg+xml"
    >=> setHttpHeader "Cache-Control" "public, max-age=30"
    >=> setHttpHeader "ETag" (Hash.sha1 body)
    >=> setBodyFromString body

let notFound msg = setStatusCode 404 >=> text msg

let packageHandler getPackageFunc slug =
    fun (next : HttpFunc) (ctx : HttpContext) ->
        task {
            let httpClientFactory = ctx.GetService<IHttpClientFactory>()
            let httpClient = httpClientFactory.CreateClient(HttpClientConfig.defaultClientName)

            let preRelease =
                match ctx.TryGetQueryStringValue "includePreReleases" with
                | Some value -> bool.Parse value
                | None       -> false
            let! package = getPackageFunc httpClient slug preRelease
            return!
                match package with
                | Some pkg ->
                    pkg
                    |> PackageModel.FromPackage
                    |> SVGs.packageSVG
                    |> renderXmlNodes
                    |> svg
                | None -> notFound "Package not found"
                <|| (next, ctx)
        }

let nugetHandler = packageHandler NuGet.getPackageAsync
let mygetHandler = packageHandler MyGet.getPackageAsync

let getBuildHistory (getBuildsFunc) (account, project) =
    fun (next : HttpFunc) (ctx : HttpContext) ->
        task {
            let includePullRequests =
                match ctx.TryGetQueryStringValue "includeBuildsFromPullRequest" with
                | Some x -> bool.Parse x
                | None   -> true
            let buildCount =
                match ctx.TryGetQueryStringValue "buildCount" with
                | Some x -> int x
                | None   -> 25
            let showStats =
                match ctx.TryGetQueryStringValue "showStats" with
                | Some x -> bool.Parse x
                | None   -> true

            let branch    = ctx.TryGetQueryStringValue "branch"
            let authToken = ctx.TryGetQueryStringValue "authToken"

            let httpClientFactory = ctx.GetService<IHttpClientFactory>()
            let httpClient = httpClientFactory.CreateClient(HttpClientConfig.defaultClientName)

            let! builds = getBuildsFunc httpClient authToken account project buildCount branch includePullRequests
            return!
                builds
                |> BuildHistoryModel.FromBuilds showStats
                |> SVGs.buildHistorySVG
                |> renderXmlNode
                |> svg
                <|| (next, ctx)
        }

let appVeyorHandler = getBuildHistory AppVeyor.getBuilds
let circleCiHandler = getBuildHistory CircleCI.getBuilds
let travisCiHandler = getBuildHistory (TravisCI.getBuilds false)

let createHandler : HttpHandler =
    fun (next : HttpFunc) (ctx : HttpContext) ->
        task {
            let plainText = (ctx.Request.Form.["plaintext"]).ToString()
            let cipherText = AES.encryptToUrlEncodedString AES.key plainText
            return! ctx.WriteTextAsync (sprintf "Encrypted auth token: %s" cipherText)
        }

let webApp =
    choose [
        GET >=>
            choose [
                route "/site.css"     >=> cssHandler Views.minifiedCss
                route "/"             >=> htmlView Views.indexView
                route "/tests"        >=> requiresApiSecret >=> htmlFile "pages/tests.html"
                route "/create"       >=> htmlFile "pages/create.html"
                route "/chars"        >=> requiresApiSecret >=> (SVGs.measureCharsSVG |> renderXmlNode |> svg)
                route "/ping"         >=> text "pong"
                routef "/nuget/%s"    nugetHandler
                routef "/myget/%s/%s" mygetHandler
                routef "/appveyor/chart/%s/%s" appVeyorHandler
                routef "/travisci/chart/%s/%s" travisCiHandler
                routef "/circleci/chart/%s/%s" circleCiHandler
            ]
        POST >=> route "/create" >=> createHandler
        notFound "Not Found" ]

// ---------------------------------
// Error handler
// ---------------------------------

let errorHandler (ex : Exception) (logger : ILogger) =
    logger.LogError(EventId(0), ex, "An unhandled exception has occurred while executing the request.")
    clearResponse >=> setStatusCode 500 >=> text ex.Message

// ---------------------------------
// Config and Main
// ---------------------------------

let configureApp (app : IApplicationBuilder) =
    app.UseGiraffeErrorHandler(errorHandler)
       .UseGiraffe(webApp)

let configureLogging (builder : ILoggingBuilder) =
    let filter (l : LogLevel) = l.Equals LogLevel.Error
    builder.AddFilter(filter).AddConsole().AddDebug() |> ignore

let configureServices (services : IServiceCollection) =
    services
        .AddGiraffe()
        .AddHttpClient(
            HttpClientConfig.defaultClientName,
            fun client ->
                client.DefaultRequestHeaders.Accept.Add(Headers.MediaTypeWithQualityHeaderValue("application/json"))
            )
            .SetHandlerLifetime(TimeSpan.FromHours 1.0)
            .AddPolicyHandler(HttpClientConfig.transientHttpErrorPolicy)
            .AddPolicyHandler(HttpClientConfig.tooManyRequestsPolicy)
            |> ignore

[<EntryPoint>]
let main _ =
    sprintf "Secret key (development): %s" devApiSecret |> Console.WriteLine
    WebHostBuilder()
        .UseKestrel()
        .UseContentRoot(Directory.GetCurrentDirectory())
        .Configure(Action<IApplicationBuilder> configureApp)
        .ConfigureLogging(configureLogging)
        .ConfigureServices(configureServices)
        .Build()
        .Run()
    0