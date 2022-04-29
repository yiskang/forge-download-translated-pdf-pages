using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Autodesk.Forge;
using Autodesk.Forge.Client;
using Autodesk.Forge.Model;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Autodesk
{
    class Program
    {
        private static string FORGE_CLIENT_ID { get; set; } = "";
        private static string FORGE_CLIENT_SECRET { get; set; } = "";
        private static readonly Scope[] SCOPES = new Scope[] {
            Scope.DataRead, Scope.DataWrite, Scope.DataCreate, Scope.DataSearch,
            Scope.BucketCreate, Scope.BucketRead, Scope.BucketUpdate, Scope.BucketDelete
        };
        protected static string AccessToken { get; private set; } = "";

        private static void ReadFromEnvOrSettings(string name, Action<string> setOutput)
        {
            string st = Environment.GetEnvironmentVariable(name);
            if (!string.IsNullOrEmpty(st))
                setOutput(st);
        }

        private static bool ReadConfigFromEnvOrSettings()
        {
            ReadFromEnvOrSettings("FORGE_CLIENT_ID", value => FORGE_CLIENT_ID = value);
            ReadFromEnvOrSettings("FORGE_CLIENT_SECRET", value => FORGE_CLIENT_SECRET = value);
            //readFromEnvOrSettings("PORT", value => PORT = value);
            //readFromEnvOrSettings("FORGE_CALLBACK", value => FORGE_CALLBACK = value);
            return true;
        }

        public static bool HttpErrorHandler(ApiResponse<dynamic> response, string msg = "", bool bThrowException = true)
        {
            if (response.StatusCode < 200 || response.StatusCode >= 300)
            {
                if (bThrowException)
                    throw new Exception(msg + " (HTTP " + response.StatusCode + ")");
                return true;
            }
            return false;
        }

        private async static Task<ApiResponse<dynamic>> OauthExecAsync()
        {
            try
            {
                AccessToken = "";
                TwoLeggedApi _twoLeggedApi = new TwoLeggedApi();
                ApiResponse<dynamic> bearer = await _twoLeggedApi.AuthenticateAsyncWithHttpInfo(
                    FORGE_CLIENT_ID,
                    FORGE_CLIENT_SECRET,
                    oAuthConstants.CLIENT_CREDENTIALS,
                    SCOPES
                );

                HttpErrorHandler(bearer, "Failed to get your token");

                AccessToken = bearer.Data.access_token;

                return (bearer);
            }
            catch (Exception ex)
            {
                Console.WriteLine("Exception when calling TwoLeggedApi.AuthenticateAsyncWithHttpInfo : " + ex.Message);
                return null;
            }
        }

        private async static Task<dynamic> GetVersionRefDerivativeUrnAsync(string projectId, string versionId, string credentials)
        {
            var versionApi = new VersionsApi();
            versionApi.Configuration.AccessToken = credentials;

            var relationshipRefs = await versionApi.GetVersionRelationshipsRefsAsync(projectId, versionId);
            if (relationshipRefs == null && relationshipRefs.included == null) return null;

            var includedData = new DynamicDictionaryItems(relationshipRefs.included);

            if (includedData.Count() > 0)
            {
                var refData = includedData.Cast<KeyValuePair<string, dynamic>>()
                    .FirstOrDefault(d => d.Value != null &&
                        d.Value.type == "versions" &&
                        d.Value.attributes.extension.type == "versions:autodesk.bim360:File");

                if (!refData.Equals(default(KeyValuePair<string, dynamic>)))
                {
                    return refData.Value.relationships.derivatives.data.id;
                }

                return null;
            }

            return null;
        }

        private async static Task<dynamic> GetVersionRefAsync(string projectId, string versionId, string credentials)
        {
            try
            {
                var versionApi = new VersionsApi();
                versionApi.Configuration.AccessToken = credentials;

                var relationshipRefs = await versionApi.GetVersionRelationshipsRefsAsync(projectId, versionId);
                if (relationshipRefs == null && relationshipRefs.data == null) return null;

                var data = new DynamicDictionaryItems(relationshipRefs.data);

                if (data.Count() > 0)
                {
                    //find meta of the reference
                    var refData = data.Cast<KeyValuePair<string, dynamic>>()
                        .FirstOrDefault(d => d.Value.meta != null &&
                            d.Value.meta.fromType == "versions" &&
                            d.Value.meta.toType == "versions");

                    if (!refData.Equals(default(KeyValuePair<string, dynamic>)))
                    {
                        if (refData.Value.meta.extension.type == "derived:autodesk.bim360:CopyDocument")
                        {
                            //this is a copy document, ref.id is the view urn, instead of version urn
                            //recurse until find the source version urn
                            var sourceViewId = refData.Value.id;
                            return await GetVersionRefAsync(projectId, sourceViewId, credentials);
                        }
                        else if (refData.Value.meta.extension.type == "derived:autodesk.bim360:FileToDocument")
                        {
                            //this is the original documents, when source model version is extracted in BIM 360 Plan folder
                            return refData.Value.id;
                        }
                        else
                        {
                            return null;
                        }
                    }
                    else
                    {
                        return null;
                    }
                }
                else
                {
                    return null;
                }
            }
            catch (Exception ex)
            {
                //Console.WriteLine("Exception when calling TwoLeggedApi.AuthenticateAsyncWithHttpInfo : " + ex.Message);
                return null;
            }
        }

        private async static Task<dynamic> GetVersionDerivativeUrnAsync(string projectId, string versionId, string credentials)
        {
            var versionsApi = new VersionsApi();
            versionsApi.Configuration.AccessToken = credentials;

            var version = await versionsApi.GetVersionAsync(projectId, versionId);

            string versionDerivativeId = null;

            if (version.data.attributes.extension.data != null && !string.IsNullOrWhiteSpace(version.data.attributes.extension.data.viewableGuid))
            {
                //var seedVersionUrn = await getVersionRefAsync(projectId, versionId, AccessToken);
                versionDerivativeId = await GetVersionRefDerivativeUrnAsync(projectId, versionId, AccessToken);
            }
            else
            {
                versionDerivativeId = (version.relationships != null && version.relationships.derivatives != null ? version.relationships.derivatives.data.id : null);
            }

            return versionDerivativeId;
        }

        private static dynamic GetViewableItem(DynamicDictionaryItems viewables, string role, string type)
        {
            foreach (KeyValuePair<string, dynamic> viewable in viewables)
            {
                dynamic viewableResource = JObject.Parse(viewable.Value.ToString());
                if (!viewableResource.ContainsKey("role") || !viewableResource.ContainsKey("type"))
                    continue;

                var viewableRole = viewable.Value.role;
                var viewableType = viewable.Value.type;

                if (viewableRole == role && viewableType == type)
                {
                    return viewable.Value;
                }
                else
                {
                    continue;
                }
            }

            return null;
        }

        private static List<dynamic> GetViewableItems(DynamicDictionaryItems viewables, string role, string type)
        {
            var items = new List<dynamic>();

            foreach (KeyValuePair<string, dynamic> viewable in viewables)
            {
                dynamic viewableResource = JObject.Parse(viewable.Value.ToString());
                if (!viewableResource.ContainsKey("role") || !viewableResource.ContainsKey("type"))
                    continue;

                var viewableRole = viewable.Value.role;
                var viewableType = viewable.Value.type;

                if (viewableRole == role && viewableType == type)
                {
                    items.Add(viewable.Value);
                }
                else
                {
                    continue;
                }
            }

            return items;
        }

        private async static Task<dynamic> DownloadDerivativeFileAsync(string urn, string fileUrn, string filePath, string credentials)
        {
            try
            {
                var derivativeApi = new DerivativesApi();
                derivativeApi.Configuration.AccessToken = credentials;

                System.IO.MemoryStream stream = await derivativeApi.GetDerivativeManifestAsync(urn, fileUrn);

                if (stream == null)
                    throw new InvalidOperationException("Failed to download AecModelData");

                stream.Seek(0, SeekOrigin.Begin);

                string folderPath = Path.GetDirectoryName(filePath);

                if (!Directory.Exists(folderPath))
                    Directory.CreateDirectory(folderPath);

                using (FileStream file = new FileStream(filePath, FileMode.Create, System.IO.FileAccess.Write))
                {
                    stream.WriteTo(file);
                    stream.Close();
                }
            }
            catch (Exception ex)
            {
                return false;
            }

            return true;
        }

        static async Task Main(string[] args)
        {
            ReadConfigFromEnvOrSettings();

            dynamic response = await OauthExecAsync();
            if (response == null)
                return;

            var itemsApi = new ItemsApi();
            var derivativeApi = new DerivativesApi();

            itemsApi.Configuration.AccessToken = AccessToken;
            derivativeApi.Configuration.AccessToken = AccessToken;

            var projectId = "b.e3269b73-141a-4e6f-8487-ff5e8f28b9cc";
            var itemId = "urn:adsk.wipprod:dm.lineage:FsWJVHw5QuG_VOj5KjTXag";

            var itemRes = await itemsApi.GetItemAsync(projectId, itemId);
            var versionId = itemRes.data.relationships.tip.data.id;

            var derivativeUrn = await GetVersionDerivativeUrnAsync(projectId, versionId, AccessToken);
            var derivativeManifest = await derivativeApi.GetManifestAsync(derivativeUrn);
            var derivatives = derivativeManifest.derivatives;
            var viewables = new DynamicDictionaryItems(derivatives[0].children);

            var pages = new List<dynamic>();

            foreach (KeyValuePair<string, dynamic> viewable in viewables)
            {
                var pageName = viewable.Value.name;
                var children = new DynamicDictionaryItems(viewable.Value.children);

                var pageFile = GetViewableItem(children, "pdf-page", "resource");
                //children.Cast<KeyValuePair<string, dynamic>>().FirstOrDefault(child => child.Value.role == "pdf-page" && child.Value.type == "resource");
                var thumbnails = GetViewableItems(children, "thumbnail", "resource");
                //children.Cast<KeyValuePair<string, dynamic>>().FirstOrDefault(child => child.Value.role == "thumbnail" && child.Value.type == "resource");

                pages.Add(new
                {
                    name = pageName,
                    file = pageFile,
                    thumbnails
                });
            }

            if (pages.Count <= 0)
                throw new InvalidOperationException("No PDF page found. Task Aborted");

            string currentPath = Path.GetDirectoryName(Assembly.GetEntryAssembly().Location);
            string folderPath = Path.Combine(currentPath, "download", derivativeUrn);

            // if (!Directory.Exists(folderPath))
            if (Directory.Exists(folderPath)) Directory.Delete(folderPath, true);
                Directory.CreateDirectory(folderPath);

            foreach (var page in pages)
            {
                try
                {
                    var derivativeFileUrn = page.file.urn;
                    var fileResult = await DownloadDerivativeFileAsync(
                        derivativeUrn,
                        derivativeFileUrn,
                        Path.Join(folderPath, derivativeFileUrn.ToString().Split(derivativeUrn.ToString())[1]),
                        AccessToken
                    );

                    if (!fileResult)
                        throw new InvalidOperationException($"Failed to download the PDF file for page {page.name}");

                    foreach (var thumbnail in page.thumbnails)
                    {
                        try
                        {
                            var thumbnailFileUrn = thumbnail.urn;
                            var thumbnailResult = await DownloadDerivativeFileAsync(
                                derivativeUrn,
                                thumbnailFileUrn,
                                Path.Join(folderPath, thumbnailFileUrn.ToString().Split(derivativeUrn.ToString())[1]),
                                AccessToken
                            );

                            if (!thumbnailResult)
                                throw new InvalidOperationException($"Failed to download the thumbnail `{string.Join("x", new DynamicDictionaryItems(((dynamic)thumbnail).resolution).Select(r => r.Value))}` for page `{page.name}`");
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine(ex.Message);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine(ex.Message);
                }
            }

        }
    }
}
