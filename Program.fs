module giraffe_graphql.App

open System
open System.IO
open FSharp.Data.GraphQL
open FSharp.Data.GraphQL.Types
open FSharp.Data.GraphQL.Execution
open Giraffe
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Cors.Infrastructure
open Microsoft.AspNetCore.Hosting
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Logging

// ---------------------------------
// Models
// ---------------------------------

type Message =
    {
        Text : string
    }

type Person = { FirstName: string; LastName: string }

let people = [ 
    { FirstName = "Jane"; LastName = "Milton" }
    { FirstName = "Travis"; LastName = "Smith" } ]

// GraphQL type definition for Person type
let Person = Define.Object("Person", [
    Define.Field("firstName", String, fun ctx p -> p.FirstName)
    Define.Field("lastName", String, fun ctx p -> p.LastName)  
])

// each schema must define so-called root query
let QueryRoot = Define.Object("Query", [
    Define.Field("people", ListOf Person, fun ctx () -> people)
])

// then initialize everything as part of schema
let schema = Schema(QueryRoot)

// Create an Exector for the schema
let executor = Executor(schema)

// ---------------------------------
// Views
// ---------------------------------

module Views =
    open GiraffeViewEngine

    let layout (content: XmlNode list) =
        html [] [
            head [] [
                title []  [ encodedText "giraffe_graphql" ]
                link [ _rel  "stylesheet"
                       _type "text/css"
                       _href "/main.css" ]
            ]
            body [] content
        ]

    let partial () =
        h1 [] [ encodedText "giraffe_graphql" ]

    let index (model : Message) =
        [
            partial()
            p [] [ encodedText model.Text ]
        ] |> layout

module GraphiqlView =
    open GiraffeViewEngine

    let index =
        html [] [
            head [] [
                link [
                    _rel "stylesheet"
                    _type "text/css"
                    _href "//unpkg.com/graphiql@0.11.11/graphiql.css"
                ]
                link [
                    _rel "stylesheet"
                    _type "text/css"
                    _href "/graphiql.css"
                ]
            ]
            body [] [
                div [ _id "app" ] []
                script [
                    _src "https://unpkg.com/react@16/umd/react.development.js"
                ] []
                script [
                    _src "https://unpkg.com/react-dom@16/umd/react-dom.development.js"
                ] []
                script [
                    _src "//unpkg.com/graphiql@0.11.11/graphiql.js"
                ] []
                script [
                    _src "/graphiql.js"
                ] []
            ]
        ]

// ---------------------------------
// Web app
// ---------------------------------

let indexHandler (name : string) =
    let greetings = sprintf "Hello %s, from Giraffe!" name
    let model     = { Text = greetings }
    let view      = Views.index model
    htmlView view

let graphiql =
    htmlView GraphiqlView.index

let query = """
    query Example {
        people {
            firstName
        }
    }
    """

let graphqlApp : HttpHandler =
    fun (next : HttpFunc) (ctx : HttpContext) ->
        let jsonSettings =
            JsonSerializerSettings()
            |> tee (fun s ->
                s.Converters <- [| OptionConverter() :> JsonConverter |]
                s.ContractResolver <- Newtonsoft.Json.Serialization.CamelCasePropertyNamesContractResolver())
        let json =
            function
            | Direct (data, _) ->
                JsonConvert.SerializeObject(data, jsonSettings)
            | Deferred (data, _, deferred) ->
                deferred |> Observable.add(fun d -> printfn "Deferred: %s" (JsonConvert.SerializeObject(d, jsonSettings)))
                JsonConvert.SerializeObject(data, jsonSettings)
            | Stream _ ->
                "{}"
        let tryParse fieldName data =
            let raw = Encoding.UTF8.GetString data
            if System.String.IsNullOrWhiteSpace(raw) |> not
            then
                let map = JsonConvert.DeserializeObject<Map<string,string>>(raw)
                match Map.tryFind fieldName map with
                | Some "" -> None
                | s -> s
            else None
        let mapString =
            JsonConvert.DeserializeObject<Map<string, obj>>
            |> Option.map
        let removeSpacesAndNewLines (str : string) = 
            str.Trim().Replace("\r\n", " ")
        let readStream (s : Stream) =
            use ms = new MemoryStream(4096)
            s.CopyTo(ms)
            ms.ToArray()
        let body = readStream ctx.Request.Body
        let query = body |> tryParse "query"
        let variables = body |> tryParse "variables" |> mapString
        match query, variables  with
        | Some query, Some variables ->
            printfn "Received query: %s" query
            printfn "Received variables: %A" variables
            let query = query |> removeSpacesAndNewLines
            let result = Schema.executor.AsyncExecute(query, variables = variables, data = Schema.root) |> Async.RunSynchronously
            printfn "Result metadata: %A" result.Metadata
            return! okWithStr (json result) next ctx
        | Some query, None ->
            printfn "Received query: %s" query
            let query = query |> removeSpacesAndNewLines
            let result = Schema.executor.AsyncExecute(query) |> Async.RunSynchronously
            printfn "Result metadata: %A" result.Metadata
            return! okWithStr (json result) next ctx
        | None, _ ->
            let result = Schema.executor.AsyncExecute(Introspection.introspectionQuery) |> Async.RunSynchronously
            printfn "Result metadata: %A" result.Metadata
            return! okWithStr (json result) next ctx
        // executor.AsyncExecute(query) |> Async.RunSynchronously
    // text "Hello World"
    //     text "Hello" next ctx
        // ctx.Body 

let webApp =
    choose [
        GET >=>
            choose [
                route "/" >=> indexHandler "world"
                routef "/hello/%s" indexHandler
                route "/graphiql" >=> graphiql
            ]
        POST >=> choose [
            route "/graphql-app" >=> graphqlApp
        ]
        setStatusCode 404 >=> text "Not Found" ]

// ---------------------------------
// Error handler
// ---------------------------------

let errorHandler (ex : Exception) (logger : ILogger) =
    logger.LogError(EventId(), ex, "An unhandled exception has occurred while executing the request.")
    clearResponse >=> setStatusCode 500 >=> text ex.Message

// ---------------------------------
// Config and Main
// ---------------------------------

let configureCors (builder : CorsPolicyBuilder) =
    builder.WithOrigins("http://localhost:8080")
           .AllowAnyMethod()
           .AllowAnyHeader()
           |> ignore

let configureApp (app : IApplicationBuilder) =
    let
        env =
            app.ApplicationServices.GetService<IHostingEnvironment>()
    in
    (match env.IsDevelopment() with
        | true  -> app.UseDeveloperExceptionPage()
        | false -> app.UseGiraffeErrorHandler errorHandler
    )
        .UseHttpsRedirection()
        .UseCors(configureCors)
        .UseStaticFiles()
        .UseGiraffe(webApp)

let configureServices (services : IServiceCollection) =
    services.AddCors()    |> ignore
    services.AddGiraffe() |> ignore

let configureLogging (builder : ILoggingBuilder) =
    let
        filter (l : LogLevel) = 
            l.Equals LogLevel.Error
    in
    builder.AddFilter(filter).AddConsole().AddDebug() |> ignore

[<EntryPoint>]
let main _ =
    let
        contentRoot =
            Directory.GetCurrentDirectory()
    let
        webRoot =
            Path.Combine(contentRoot, "WebRoot")
    in
    WebHostBuilder()
        .UseKestrel()
        .UseContentRoot(contentRoot)
        .UseIISIntegration()
        .UseWebRoot(webRoot)
        .Configure(Action<IApplicationBuilder> configureApp)
        .ConfigureServices(configureServices)
        .ConfigureLogging(configureLogging)
        .Build()
        .Run()
    0