#r "Newtonsoft.Json"

using System;
using System.IO;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Net;
using System.Net.Http;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RestSharp;

public static async Task<object> Run(HttpRequestMessage req, TraceWriter log)
{
    log.Info($"Webhook was triggered!");

    string jsonContent = await req.Content.ReadAsStringAsync();
    dynamic webhook = JsonConvert.DeserializeObject(jsonContent);   // Converts string to actual json object
    string transCheck = startTransfer(webhook);

    return req.CreateResponse(HttpStatusCode.OK, new
    {
        result = transCheck
    });
}

public static string startTransfer(dynamic webhookData)
{
    // Set up some data we need
    string evFilePath = webhookData["path"];
    string evUri = "https://api.exavault.com/v1/";
    string evFileName = Path.GetFileName((string)webhookData.path);
    string dbUri = "https://api.dropboxapi.com/2/";
    // string dbDestination = "";   // Use this to configure a destination that is not identical to source path
    try     // Get secret data from auth.txt file
    {
        using(StreamReader file = new StreamReader(@"../ExavaultTransfer/auth.json"))
        {
            dynamic json = JsonConvert.DeserializeObject(file.ReadLine());
        }
    }
    catch(Exception e)
    {
        Console.WriteLine("The auth.txt file could not be read:");
        Console.WriteLine(e.Message);
    }
    string evUsername = json["username"];      // Turns out the inside of try is out of scope, so these need to go here
    string evPassword = json["password"];
    string evApiKey = json["evApiKey"];
    string dbAccessToken = json["dbAccessToken"];

    if(webhookData["operation"] == "Upload" && webhookData["accountname"] == evUsername)
    {
        // Authenticate with Exavault API to get accessToken
        string evAuthToken = postEvToken(evUri, evApiKey, evUsername, evPassword);
        // Get Exavault file download URL
        string evDownUrl = getDownUrl(evUri, evApiKey, evAuthToken, evFilePath, evFileName);
        // Send exavault download URL to Dropbox using uri+/save_url/auto/<path>
        string dbJob = postDbUrl(dbUri, dbAccessToken, evDownUrl, evFilePath);
        // Check download until returns status true
        bool dlFinished = false;
        int count = 0;
        while(count < 5)
        {
            dlFinished = postDbCheck(dbUri, dbAccessToken, dbJob);
            if(dlFinished)
            {
                break;
            }
            System.Threading.Thread.Sleep(5000);    // Wait for 5 seconds between checks
            count += 1;
        }
        // Delete file from Exavault
        if(dlFinished == true)
        {
            bool deleteCheck = getDeleteEv(evUri, evApiKey, evAuthToken, evFilePath, evFileName);
        }
        // De-authenticate with exavault (and Dropbox if we had authenticated here)
        bool deAuth = postDeAuth(evUri, evApiKey, evAuthToken);
        if(deAuth)
        {
            return "success";
        }
        else
        {
            return "failure";
        }
    }
    else
    {
        return "invalid webhook data";
    }
}

public static string postEvToken(string uri, string api_key, string evUsername, string evPassword)
{
    try
    {
        var client = new RestClient(uri+"authenticateUser");
        var request = new RestRequest(Method.POST);
        request.AddHeader("api_key", api_key);
        request.AddHeader("Content-Type", "application/x-www-form-urlencoded")
        request.AddParameter("username", evUsername);
        request.AddParameter("password", evPassword);
        IRestResponse response = client.Execute(request);    // Returns IRestResponse
        dynamic json = JsonConvert.DeserializeObject(response.Content); // Need to convert IRestResponse to json
        return (string)json["results"]["accessToken"];  // Only want the accessToken, so that's all that returns
    }
}

public static string getDownUrl(string uri, string api_key, string authToken, string filePath, string fileName)
{
    string builtUri = uri+"getDownloadFileUrl?access_token="+authToken+"&filePaths="+filePath+"&downloadName="+fileName+"`";
    var client = new RestClient(builtUri);
    var request = new RestRequest(Method.GET);
    request.AddHeader("api_key", api_key);
    IRestResponse response = client.Execute(request);
    dynamic json = JsonConvert.DeserializeObject(response.Content); // Need to convert IRestResponse to json
    return (string)json["results"]["url"];  // Only want the downloadURL, so that's all that returns
}

public static string postDbUrl(string uri, string token, string downUrl, string dbPath)
{
    string builtUri = uri + "files/save_url";
    var client = new RestClient(builtUri);
    var request = new RestRequest(Method.POST);
    request.AddParameter("Authorization", "Bearer " + token, ParameterType.HttpHeader);
    request.AddParameter("Content-Type", "application/json", ParameterType.HttpHeader);
    request.AddJsonBody(new { url = downUrl, path = dbPath});
    IRestResponse response = client.Execute(request);    // Returns IRestResponse
    dynamic json = JsonConvert.DeserializeObject(response.Content);
    return (string)json["async_job_id"];
}

public static bool postDbCheck(string uri, string token, string job)
{
    string builtUri = uri + "files/save_url/check_job_status";
    var client = new RestClient(builtUri);
    var request = new RestRequest(Method.POST);
    request.AddParameter("Authorization", "Bearer " + token, ParameterType.HttpHeader);
    request.AddParameter("Content-Type", "application/json", ParameterType.HttpHeader);
    request.AddJsonBody(new { async_job_id = job });
    IRestResponse response = client.Execute(request);    // Returns IRestResponse
    dynamic json = JsonConvert.DeserializeObject(response.Content);
    if(json[".tag"] == "complete")
    {
        return true;
    }
    else
    {
        return false;
    }
}

public static bool getDeleteEv(string uri, string api_key, string authToken, string filePath, string fileName)
{
    string builtUri = uri+"deleteResources?access_token="+authToken+"&filePaths[]="+filePath;
    var client = new RestClient(builtUri);
    var request = new RestRequest(Method.GET);
    request.AddHeader("api_key", api_key);
    IRestResponse response = client.Execute(request);
    dynamic json = JsonConvert.DeserializeObject(response.Content); // Need to convert IRestResponse to json
    if((int)json["success"] == 1)
    {
        return true;
    }
    else
    {
        return false;
    }
}

public static bool postDeAuth(string uri, string api_key, string token)
{
    var client = new RestClient(uri+"logoutUser");
    var request = new RestRequest(Method.POST);
    request.AddHeader("api_key", api_key);
    request.AddHeader("Content-Type", "application/x-www-form-urlencoded");
    request.AddParameter("application/x-www-form-urlencoded", $"access_token={token}", ParameterType.RequestBody);
    IRestResponse response = client.Execute(request);    // Returns IRestResponse
    dynamic json = JsonConvert.DeserializeObject(response.Content); // Need to convert IRestResponse to json
    if((int)json["success"] == 1)
    {
        return true;
    }
    else
    {
        return false;
    }
}