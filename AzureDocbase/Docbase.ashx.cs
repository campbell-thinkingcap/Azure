using System;
using System.Collections.Generic;
using System.Linq;
using System.Web;
using Microsoft.Azure.Documents.Client;
using Microsoft.Azure.Documents.Linq;
using Microsoft.Azure.Documents;
using System.Configuration;

using Microsoft.WindowsAzure.Storage;
using Microsoft.WindowsAzure.Storage.Auth;
using Microsoft.WindowsAzure.Storage.Blob;
using Microsoft.WindowsAzure.Storage.Shared.Protocol;

using System.Threading.Tasks;
using System.Threading;
using System.Web.Script.Serialization;
using System.IO;
using System.Globalization;
using System.Diagnostics;



namespace AzureDocbase
{
    /// <summary>
    /// Summary description for Docbase
    /// </summary>
    public class Docbase : HttpTaskAsyncHandler 
    {
        //private static string endpointUrl = "https://nalogs.documents.azure.com:443/";
        private static string endpointUrl = "https://europelogs.documents.azure.com:443/";
        //private static string authorizationKey = "+en42EHbj+CG2xP6luU0Jxubsh7Cbg25b16DlndTQ5fIO5cmL47cRTUC0kYS1xf8LvMjL4DJqVi4sOcaupZcnQ==";
        private static string authorizationKey = "ia3rgNZdakj1jfsrZSoaPejh53mLk3MH7w6ByvpSbl/SWotOQU8iOxZLuLDwydkaECqDnxp1ryRCjrbpywZOJw==";
        public string response;

        
        public override async Task ProcessRequestAsync(HttpContext context)
        {
            var client = new DocumentClient(new Uri(endpointUrl), authorizationKey);

            string method = context.Request["op"].ToString();
            context.ThreadAbortOnTimeout = false;
            context.Response.ContentType = "text/json";
            switch (method){
                case "create" : await createDB(context, client);break;
                case "createDoc": await CreateDocuments(context, client); break;
                case "count": await RunCount(context, client); break;
                case "client": getClientList(context, client); break;
                case "clientsproc": await GetClientsSproc(context, client); break;
                case "clientsessions": await getClientSessions(context, client); break;
                case "allsessionlogs": getSessionLogs(context, client); break;
                case "archiveSession": archiveSessionLogs(context, client); break;
                default: context.Response.Write("{\"response\":\"nothing\"}");
                context.Response.Flush();
                break;
            }            

        } 
        
       
       
         async Task createDB(HttpContext context,DocumentClient client)
        {
            string response = "";
            try
            {

                
                Database database = await client.CreateDatabaseAsync(
                new Database
                {
                    Id = "Logs"
                });
                //Create a document collection 
                DocumentCollection documentCollection = new DocumentCollection
                {
                    Id = "APILOG"
                };

                documentCollection = await client.CreateDocumentCollectionAsync(database.SelfLink, documentCollection); 
                response = "{\"response\":\"yeah\"}";
            }
            catch (Exception ex)
            {
                response = "{\"response\":\"" + ex.Message + "\"}";
            }
            context.Response.Write(response);
            context.Response.Flush();
        }

         async Task CreateDocuments(HttpContext context, DocumentClient client)
         {
             var databases = client.CreateDatabaseQuery().Where(db => db.Id == "Logs").ToArray();

             if (databases.Any())
             {
                 var collections = client.CreateDocumentCollectionQuery(databases.First().SelfLink).Where(col => col.Id == "APILOG").ToArray();
                 var collection = collections.First().SelfLink;
                 string json = "{ \"type\" : \"APILog\", \"B\" : \"2\", \"C\" : \"3\" }";
                 SortedList<string, object> jsonObj = new SortedList<string, object>();
                 
                 jsonObj = new JavaScriptSerializer().Deserialize<SortedList<string, object>>(json);
                 await client.CreateDocumentAsync(collection, jsonObj);
             }
             
             string response = "";
             context.Response.Write(response);
             context.Response.Flush();
         }

         async Task GetClientsSproc(HttpContext context, DocumentClient client)
         {
             var databases = client.CreateDatabaseQuery().Where(db => db.Id == "Logs").ToArray();
             var collections = client.CreateDocumentCollectionQuery(databases.First().SelfLink).Where(col => col.Id == "APILOG").ToArray();
             var collection = collections.First().SelfLink;

             List<string> jsonObj = new List<string>();
            if (databases.Any())
             {
             string body = File.ReadAllText(context.Server.MapPath("js/clients.js"));         
                 
            StoredProcedure sproc = new StoredProcedure
             {
                 Id = "ClientList",
                 Body = body
             };
             await TryDeleteStoredProcedure(collection, sproc.Id, client);
             sproc = await client.CreateStoredProcedureAsync(collection, sproc);

             // 2. Prepare to run stored procedure. 
             string orderByFieldName = "client";
             var filterQuery = string.Format(CultureInfo.InvariantCulture, "SELECT log.client FROM APILOG log WHERE log.client != ''", orderByFieldName);
             // Note: in order to do a range query (> 10) on this field, the collection must have a range index set for this path (see ReadOrCreateCollection).

             string continuationToken = null;
             string uniqueClient = "";
               
             var count = 0;

             // 3. The script has limit on how long it can run.
             //    To account for that when it runs near timeout or there are too many docs, the script will return
             //    and provide current count and continuation token. We will call it again and it will continue from where it left
             //    (this will be separate transaction ).
             do
             {
                 // 4. The script sends response as object with 2 properties: count and continuationToken. 
                 //    { continuationToken, count }.
                 //    We could either create C# class for this and cast the result to it, or use 'dynamic'.
                 //    For simpicity, use 'dynamic'. 
                 var response = (await client.ExecuteStoredProcedureAsync<dynamic>(sproc.SelfLink, filterQuery, continuationToken,uniqueClient)).Response;

                 // Get continuation token which is set by the script.
                 continuationToken = (string)response.continuationToken;
                 uniqueClient = (string)response.uniqueClient;
                 for (int xx = 0; xx < response.clients.Count;xx++ )
                     jsonObj.Add((string)response.clients[xx]);
                 // Get count from current continuation and accumulate into local variable 'count'.
                 count += (int)response.count;


                 // 5. Iterate until the script stops passing continuation tokens.
             } while (!string.IsNullOrEmpty(continuationToken));

             List<string> ClientList = new List<string>();

             ClientList.AddRange(jsonObj.Distinct());

             string jsonresponse = "{\"response\" : \"" + count.ToString() + "\",\"clients\" : " + new JavaScriptSerializer().Serialize(ClientList) + "}";
             context.Response.Write(jsonresponse);
             await TryDeleteStoredProcedure(collection, sproc.Id, client);

             context.Response.Flush();
         }

         }

         private static async Task TryDeleteStoredProcedure(string colSelfLink, string sprocId, DocumentClient client)
         {
             StoredProcedure sproc = client.CreateStoredProcedureQuery(colSelfLink).Where(s => s.Id == sprocId).AsEnumerable().FirstOrDefault();
             if (sproc != null)
             {
                 await client.DeleteStoredProcedureAsync(sproc.SelfLink);
             }
         }

        /// <summary>
        /// Gets the list of clients, returns client id's
        /// </summary>
        /// <param name="context"></param>
        /// <param name="client"></param>
         void getClientList(HttpContext context, DocumentClient client)
         {
             var databases = client.CreateDatabaseQuery().Where(db => db.Id == "Logs").ToArray();
             var collections = client.CreateDocumentCollectionQuery(databases.First().SelfLink).Where(col => col.Id == "APILOG").ToArray();
             var collection = collections.First().SelfLink;

             List<string> jsonObj = new List<string>();
             string clientid = "";
             foreach (var l in client.CreateDocumentQuery(collection, "SELECT log.client FROM APILOG log WHERE log.client != ''"))
             {
                 if (l.client != clientid)
                    jsonObj.Add(l.ToString());

                 clientid = l.client;
             }
             context.Response.Write(new JavaScriptSerializer().Serialize(jsonObj));
             context.Response.Flush();
         }

        /// <summary>
        /// Gets a list of client sessions
        /// </summary>
        /// <param name="context"></param>
        /// <param name="client"></param>
         async Task getClientSessions(HttpContext context, DocumentClient client)
         {
             var databases = client.CreateDatabaseQuery().Where(db => db.Id == "Logs").ToArray();
             var collections = client.CreateDocumentCollectionQuery(databases.First().SelfLink).Where(col => col.Id == "APILOG").ToArray();
             var collection = collections.First().SelfLink;

             List<string> jsonObj = new List<string>();
             if (databases.Any())
             {
                 string body = File.ReadAllText(context.Server.MapPath("js/sessions.js"));

                 StoredProcedure sproc = new StoredProcedure
                  {
                      Id = "SessionList",
                      Body = body
                  };
                 await TryDeleteStoredProcedure(collection, sproc.Id, client);
                 sproc = await client.CreateStoredProcedureAsync(collection, sproc);

                 // 2. Prepare to run stored procedure. 
                 string orderByFieldName = "client";
                 var filterQuery = string.Format(CultureInfo.InvariantCulture, "SELECT log.SessionID, log.Timestamp, log.StudentName FROM APILOG log WHERE log.client = '"+context.Request["client"].ToString()+"'", orderByFieldName);
                 // Note: in order to do a range query (> 10) on this field, the collection must have a range index set for this path (see ReadOrCreateCollection).

                 string continuationToken = null;
                 string uniqueClient = "";

                 var count = 0;

                 // 3. The script has limit on how long it can run.
                 //    To account for that when it runs near timeout or there are too many docs, the script will return
                 //    and provide current count and continuation token. We will call it again and it will continue from where it left
                 //    (this will be separate transaction ).
                 do
                 {
                     // 4. The script sends response as object with 2 properties: count and continuationToken. 
                     //    { continuationToken, count }.
                     //    We could either create C# class for this and cast the result to it, or use 'dynamic'.
                     //    For simpicity, use 'dynamic'. 
                     var response = (await client.ExecuteStoredProcedureAsync<dynamic>(sproc.SelfLink, filterQuery, continuationToken, uniqueClient)).Response;

                     // Get continuation token which is set by the script.
                     continuationToken = (string)response.continuationToken;
                     uniqueClient = (string)response.uniqueClient;
                     for (int xx = 0; xx < response.sessions.Count; xx++)
                         jsonObj.Add((string)response.sessions[xx]);
                     // Get count from current continuation and accumulate into local variable 'count'.
                     count += (int)response.count;


                     // 5. Iterate until the script stops passing continuation tokens.
                 } while (!string.IsNullOrEmpty(continuationToken));

                 List<string> ClientList = new List<string>();

                 ClientList.AddRange(jsonObj.Distinct());
                 
                 JavaScriptSerializer clients_serialized = new JavaScriptSerializer();
                 clients_serialized.MaxJsonLength = Int32.MaxValue;
                 string jsonresponse = "{\"response\" : \"" + count.ToString() + "\",\"sessions\" : " + clients_serialized.Serialize(ClientList) + "}";
                 context.Response.Write(jsonresponse);
                 await TryDeleteStoredProcedure(collection, sproc.Id, client);

                 context.Response.Flush();

             }
         }

        /// <summary>
        /// This will return the count of documents using a stored prucedure
        /// </summary>
        /// <param name="context"></param>
        /// <param name="client"></param>
        /// <returns></returns>
         async Task RunCount(HttpContext context, DocumentClient client)
         {
             var databases = client.CreateDatabaseQuery().Where(db => db.Id == "Logs").ToArray();
             var collections = client.CreateDocumentCollectionQuery(databases.First().SelfLink).Where(col => col.Id == "APILOG").ToArray();
             var collection = collections.First().SelfLink;

             StoredProcedure checksprock = client.CreateStoredProcedureQuery(collection).Where(s => s.Id == "Count").AsEnumerable().FirstOrDefault();
             if (checksprock == null) {
                 string body = File.ReadAllText(context.Server.MapPath("js/count.js"));
                 checksprock = new StoredProcedure
                 {
                     Id = "Count",
                     Body = body
                 };
                 await client.CreateStoredProcedureAsync(collection, checksprock);
             }
             // dont' think we need this unless we delete the stored procedure
             //else
             //{
             //    await client.DeleteStoredProcedureAsync(checksprock.SelfLink);
             //    string body = File.ReadAllText(context.Server.MapPath("js/count.js"));
             //    checksprock = new StoredProcedure
             //    {
             //        Id = "Count",
             //        Body = body
             //    };
             //    await client.CreateStoredProcedureAsync(collection, checksprock);
             //}

             var filterQuery = string.Empty;             // Can use something like "SELECT 0 FROM root r";
             var continuationToken = string.Empty;       // Start off with this empty or null
             var count = 0;

             // 3. The script has limit on how long it can run.
             //    To account for that when it runs near timeout or there are too many docs, the script will return
             //    and provide current count and continuation token. We will call it again and it will continue from where it left
             //    (this will be separate transaction ).
             do
             {
                 // 4. The script sends response as object with 2 properties: count and continuationToken. 
                 //    { continuationToken, count }.
                 //    We could either create C# class for this and cast the result to it, or use 'dynamic'.
                 //    For simpicity, use 'dynamic'. 
                 var response = (await client.ExecuteStoredProcedureAsync<dynamic>(checksprock.SelfLink, filterQuery, continuationToken)).Response;

                 // Get continuation token which is set by the script.
                 continuationToken = (string)response.continuationToken;

                 // Get count from current continuation and accumulate into local variable 'count'.
                 count += (int)response.count;
                

                 // 5. Iterate until the script stops passing continuation tokens.
             } while (!string.IsNullOrEmpty(continuationToken));
             string jsonresponse = "{\"response\" : \"" + count.ToString() + "\"}";
             context.Response.Write(jsonresponse);
             context.Response.Flush();
             
             
            
         }

         void getSessionLogs(HttpContext context, DocumentClient client)
         {
             var databases = client.CreateDatabaseQuery().Where(db => db.Id == "Logs").ToArray();
             var collections = client.CreateDocumentCollectionQuery(databases.First().SelfLink).Where(col => col.Id == "APILOG").ToArray();
             var collection = collections.First().SelfLink;

             List<string> jsonObj = new List<string>();
             string sessionID = context.Request["sessionid"].ToString();
             foreach (var l in client.CreateDocumentQuery(collection, "SELECT * FROM APILOG log WHERE log.SessionID ='"+sessionID+"'"))
             {
                 
                     jsonObj.Add(l.ToString());

                
             }
             context.Response.Write(new JavaScriptSerializer().Serialize(jsonObj));
             context.Response.Flush();
         }

         void archiveSessionLogs(HttpContext context, DocumentClient client)
         {
             var databases = client.CreateDatabaseQuery().Where(db => db.Id == "Logs").ToArray();
             var collections = client.CreateDocumentCollectionQuery(databases.First().SelfLink).Where(col => col.Id == "APILOG").ToArray();
             var collection = collections.First().SelfLink;

             List<string> jsonObj = new List<string>();
             string sessionID = context.Request["sessionid"].ToString();
             foreach (var l in client.CreateDocumentQuery(collection, "SELECT * FROM APILOG log WHERE log.SessionID ='" + sessionID + "'"))
             {

                 jsonObj.Add(l.ToString());


             }

             // Lets write this to the blobStorage
            
             CloudStorageAccount storageAccount = CloudStorageAccount.Parse(ConfigurationManager.AppSettings["StorageConnectionString"].ToString());
             CloudBlobClient blobClient = storageAccount.CreateCloudBlobClient();

             CloudBlobContainer container = blobClient.GetContainerReference("apilogs");
             container.CreateIfNotExists();

             CloudBlockBlob blockBlob = container.GetBlockBlobReference(sessionID);

             blockBlob.UploadText(new JavaScriptSerializer().Serialize(jsonObj));

             context.Response.Write("{\"response\":\"archived\"}");
             context.Response.Flush();
         }


         internal class OrderByResult
         {
             public Document[] Result { get; set; }
             public int? Continuation { get; set; }
         }

        public bool IsReusable
        {
            get
            {
                return false;
            }
        }
    }

}