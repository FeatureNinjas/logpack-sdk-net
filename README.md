# LogPack - ASP.net Core package

This repository contains the lib to use LogPack in ASP.net Core.


# Prerequisites

- ASP.net core 3.2+ 

# Installation

- Download the NuGet package from the Releases in this repository
- Install the NuGet package in your asp.net core project and add the required configuration into the `Configure()` methods of `Startup.cs`

``` cs
// This method gets called by the runtime. Use this method to configure the HTTP request pipeline.
public void Configure(IApplicationBuilder app, IWebHostEnvironment env)
{
    // ...
    app.UseLogPack(new LogPackOptions()
    {
        Sinks = new LogPackSink[]
        {
            new FtpSink(
                [ftp-server-url], 
                [ftp-server-port], 
                [ftp-server-username], 
                [ftp-server-password], 
        },
        ProgramType = typeof(Program)
    });
    // ...
    app.UseRouting();
}
```

# Additional Features

## Include Filters

By default, a log pack is created and uploaded whenever the request responds with a 5xx return code. You can use include filters (even create your own) to change this. For example, to create a log pack for all return code 3xx, 4xx and 5xx, add the following lines to the `LogPackOptions` object (see above)

``` cs
Include = new IIncludeFilter[]
{
  new StatusIncludeFilter(0, 1000),
},
```
    
In order to create a custom include filter, implement the `IIncludeFilter` interface. Example:

``` cs
public class LogPackAccountIdIncludeFilter : IIncludeFilter
{
  public bool Include(HttpContext context)
  {
      if (context.Items.ContainsKey("accountId")
          && (context.Items["accountId"].ToString() == "1234"
          || context.Items["accountId"].ToString() == "5678"))
      {
          return true;
      }

      return false;
  }
}
```
    
If this include filter is added to the log pack options, then for all requests for the user with the account ID is 1234 or 5678, a log pack is created.

You could even base include filters on feature flags, e.g. by using FeatureNinjas ;).

## Exclude Filters

## Notifications
