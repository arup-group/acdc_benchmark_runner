using Microsoft.Identity.Client;
using RestSharp;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace runner
{
    internal class Program
    {
        private static byte[] s_aditionalEntropy = { 1, 3, 6, 0, 0, 6 };

        // The MSAL Public client app
        private static IPublicClientApplication application;

        static void Main(string[] args)
        {
            Console.WriteLine("benchmarking ac/dc tool");
            MainAsync(args).GetAwaiter().GetResult();
            Console.ReadLine();
        }

        private static async System.Threading.Tasks.Task MainAsync(string[] args)
        {
            if (args.Length == 0 || args.Length != 3)
            {
                Console.WriteLine("empty or insufficient args. please provide like below:\r\n\trunner.exe <ac/tda/tda_async> <simple/walldesignersummary> <2/10/100/1000/10000>");
                return;
            }

            var hostArg = args[0];
            var methodArg = args[1];
            var sizeArg = args[2];

            var host = TestHost.ArupCompute;
            var method = TestMethod.Simple;
            var size = 10;

            if (hostArg == "ac")
            {
                host = TestHost.ArupCompute;
            }
            else if (hostArg == "tda")
            {
                host = TestHost.TDA_Hosted_DC;
            }
            else if (hostArg == "tda_async")
            {
                host = TestHost.TDA_Hosted_DC_Async;
            }

            if (methodArg == "simple")
            {
                method = TestMethod.Simple;
            }
            else if (methodArg == "walldesignersummary")
            {
                method = TestMethod.WallDesignerSummary;
            }

            size = Convert.ToInt32(sizeArg);

            var tokenPath = "token.dat";

            AuthenticationResult authResult = null;
            var prevAuthResult = GetAuthResult(tokenPath);

            if (prevAuthResult != null)
            {
                if (prevAuthResult.ExpiresOn > DateTimeOffset.Now)
                {
                    Console.WriteLine($"using previous token (expires on: {prevAuthResult.ExpiresOn.ToLocalTime()})");
                    authResult = prevAuthResult;
                }
                else
                {
                    Console.WriteLine($"previous token expired on: {prevAuthResult.ExpiresOn.ToLocalTime()}");

                    // We intend to obtain a token for Graph for the following scopes (permissions)
                    authResult = await SignInUserAndGetTokenUsingMSAL();
                }
            }
            else
            {
                authResult = await SignInUserAndGetTokenUsingMSAL();
            }

            if (authResult == null)
            {
                Console.WriteLine("no valid token");
            }
            else
            {
                if (authResult != prevAuthResult)
                {
                    CacheAuthResult(authResult, tokenPath);
                }

                var datetimeNow = DateTime.Now;
                var result = await RunBenchmarkAsync(host, method, size, authResult.AccessToken);
                File.AppendAllLines("results.csv", new string[] { $"{datetimeNow.ToShortDateString()} {datetimeNow.ToShortTimeString()},{host},{method},{size},{result}" });
                Console.WriteLine(result);
            }

            Console.WriteLine("press a key to finish");

        }

        /// <summary>
        /// Signs in the user using the device code flow and obtains an Access token for MS Graph
        /// </summary>
        /// <param name="configuration"></param>
        /// <param name="scopes"></param>
        /// <returns></returns>
        private static async Task<AuthenticationResult> SignInUserAndGetTokenUsingMSAL()
        {
            string[] scopes = new[] { "user.read", "api://tda-app-dev-api/user_impersonation" };

            // Initialize the MSAL library by building a public client application
            application = PublicClientApplicationBuilder.Create("e6a6969e-064c-4125-8629-85f37a31cec7")
                .WithAuthority("https://login.microsoftonline.com/" + "arup.onmicrosoft.com")
                .WithRedirectUri("msale6a6969e-064c-4125-8629-85f37a31cec7://auth")
                .Build();

            AuthenticationResult result = null;
            var accounts = await application.GetAccountsAsync();

            try
            {
                // Try to acquire an access token from the cache, if UI interaction is required, MsalUiRequiredException will be thrown.
                result = await application.AcquireTokenSilent(scopes, accounts.FirstOrDefault()).ExecuteAsync();
            }
            catch (MsalUiRequiredException ex)
            {
                try
                {
                    // Acquiring an access token interactively using the custom html.
                    result = await application.AcquireTokenInteractive(scopes)
                        .WithAccount(accounts.FirstOrDefault())
                        .WithPrompt(Prompt.SelectAccount)
                        .ExecuteAsync();
                }
                catch (MsalException msalex)
                {
                    Console.WriteLine($"ERROR: {msalex.Message}");
                }
            }

            return result;
        }


        static async Task<double> RunBenchmarkAsync(TestHost testHost, TestMethod testMethod
            , int batchSize, string token)
        {
            double result = 0;

            string input = "{" + $"\"test_host\":\"{testHost.ToString().Replace("_", " ")}\",\"test_method\":\"{testMethod.ToString()}\",\"test_batchsize\":{batchSize}" + "}";

            var clientOptions = new RestClientOptions("https://tdal-dev-function-app-ds15.azurewebsites.net/api/test/run_dc_benchmark")
            {
                Timeout = -1
            };
            var client = new RestClient(clientOptions);
            var request = new RestRequest();
            request.AddHeader("Authorization", $"Bearer {token}");
            request.AddHeader("Content-Type", "text/plain");
            var body = input;
            request.AddParameter("text/plain", body, ParameterType.RequestBody);
            Console.WriteLine("sending request");
            var response = await client.ExecutePostAsync(request);
            Console.WriteLine(response.Content);
            string elapsedLabel = "'elapsed':";
            if (response == null)
            {
                Console.WriteLine("response is null");
                return double.NaN;
            }
            else if (response.Content == null || response.Content.Length == 0)
            {
                Console.WriteLine($"empty or null content. (status code: {response.StatusCode})");
                return double.NaN;
            }
            if (!response.Content.Contains(elapsedLabel))
            {
                Console.WriteLine($"cannot find {elapsedLabel} in response content:");
                Console.WriteLine(response.Content);
                return double.NaN;
            }
            var elapsedIndex = response.Content.IndexOf(elapsedLabel);
            var endOfElapsedValueIndex = response.Content.IndexOf(",", elapsedIndex);
            if (endOfElapsedValueIndex == -1)
            {
                endOfElapsedValueIndex = response.Content.IndexOf("}", elapsedIndex);
            }
            if (endOfElapsedValueIndex == -1)
            {
                return double.NaN;
            }
            int startOfElapsedValueIndex = elapsedIndex + elapsedLabel.Length;
            var elapsedTxt = response.Content.Substring(startOfElapsedValueIndex, endOfElapsedValueIndex - startOfElapsedValueIndex);
            result = Convert.ToDouble(elapsedTxt);
            //File.WriteAllText("C:\\temp\\result.txt", response.Content);
            //Console.WriteLine(response.Content);
            //var content = Newtonsoft.Json.JsonConvert.DeserializeObject<dynamic>(response.Content);
            //result = Convert.ToDouble(content.elapsed);
            return result;
        }

        /// <summary>
        /// Caches refresh token securely
        /// </summary>
        /// <param name="authResult">refresh token</param>
        static void CacheAuthResult(AuthenticationResult authResult, string tokenPath)
        {
            AuthenticationResultSimplified authResultSimplified = new AuthenticationResultSimplified() { AccessToken = authResult.AccessToken, ExpiresOn = authResult.ExpiresOn };
            var authResultTxt = Newtonsoft.Json.JsonConvert.SerializeObject(authResultSimplified);
            var tokenBytes = Encoding.ASCII.GetBytes(authResultTxt);
            byte[] protectedBytes = ProtectedData.Protect(tokenBytes, s_aditionalEntropy, DataProtectionScope.CurrentUser);
            File.WriteAllBytes(tokenPath, protectedBytes);
        }

        /// <summary>
        /// Retrieves refresh token from secured cache 
        /// </summary>
        /// <returns></returns>
        static AuthenticationResult GetAuthResult(string tokenPath)
        {
            try
            {
                if (File.Exists(tokenPath))
                {
                    var protectedBytes = File.ReadAllBytes(tokenPath);
                    var bytes = ProtectedData.Unprotect(protectedBytes, s_aditionalEntropy, DataProtectionScope.CurrentUser);
                    var authResultTxt = Encoding.ASCII.GetString(bytes);
                    var authResultSimplified = Newtonsoft.Json.JsonConvert.DeserializeObject<AuthenticationResultSimplified>(authResultTxt);
                    var authResult = new AuthenticationResult(authResultSimplified.AccessToken, false, "", authResultSimplified.ExpiresOn, DateTimeOffset.MaxValue, "", null, "", null, Guid.Empty);
                    return authResult;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.Message);
            }
            return null;
        }

        enum TestHost
        {
            ArupCompute,
            TDA_Hosted_DC,
            TDA_Hosted_DC_Async
        }

        enum TestMethod
        {
            Simple,
            WallDesignerSummary
        }
    }
}
