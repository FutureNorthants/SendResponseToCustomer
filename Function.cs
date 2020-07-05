using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using System.Web;
using Amazon;
using Amazon.Lambda.Core;
using Amazon.S3;
using Amazon.S3.Model;
using Amazon.SQS;
using Amazon.SQS.Model;
using Amazon.StepFunctions;
using Amazon.StepFunctions.Model;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

[assembly: LambdaSerializer(typeof(Amazon.Lambda.Serialization.Json.JsonSerializer))]

namespace SendResponseToCustomer
{
    public class Function
    {
        private static readonly RegionEndpoint bucketRegion = RegionEndpoint.EUWest1;
        private static readonly RegionEndpoint sqsRegion = RegionEndpoint.EUWest1;

        public async Task FunctionHandler(object input, ILambdaContext context)
        {
            String instance = Environment.GetEnvironmentVariable("instance");
            String cxmEndPoint;
            String cxmAPIKey;
            //TODO Use secrets manager
            //TODO change default to error trap
            //TODO Use lambda variable instead of instance
            switch (instance.ToLower())
            {
                case "live":
                    cxmEndPoint = Environment.GetEnvironmentVariable("cxmEndPointLive");
                    cxmAPIKey = Environment.GetEnvironmentVariable("cxmAPIKeyLive");
                    break;
                default:
                    cxmEndPoint = Environment.GetEnvironmentVariable("cxmEndPointTest");
                    cxmAPIKey = Environment.GetEnvironmentVariable("cxmAPIKeyTest");
                    break;
            }
            JObject o = JObject.Parse(input.ToString());
            String caseReference = (string)o.SelectToken("CaseReference");
            String taskToken = (string)o.SelectToken("TaskToken");
            Console.WriteLine("caseReference : " + caseReference);
            CaseDetails caseDetails = await GetStaffResponseAsync(cxmEndPoint, cxmAPIKey, caseReference, taskToken);
            try
            {
                if (!String.IsNullOrEmpty(caseDetails.staffResponse))
                {
                    String emailBody = await FormatEmailAsync(caseReference, caseDetails, taskToken);
                    if (!String.IsNullOrEmpty(emailBody))
                    {
                        //TODO remove nortbert hardcoding
                        if(await SendMessageAsync(caseReference, taskToken,emailBody,caseDetails,"norbert@northampton.digital"))
                        {
                            await SendSuccessAsync(taskToken);
                        }                       
                    }
                }
            }
            catch (Exception)
            {
            }
                   
            Console.WriteLine("Completed");
        }

        private async Task<CaseDetails> GetStaffResponseAsync(String cxmEndPoint, String cxmAPIKey, String caseReference, String taskToken)
        {
            CaseDetails caseDetails = new CaseDetails();
            HttpClient cxmClient = new HttpClient();
            cxmClient.BaseAddress = new Uri(cxmEndPoint);
            string requestParameters = "key=" + cxmAPIKey;
            HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, "/api/service-api/norbert/case/" + caseReference + "?" + requestParameters);
            try
            {
                HttpResponseMessage response = cxmClient.SendAsync(request).Result;
                if (response.IsSuccessStatusCode)
                {                 
                    HttpContent responseContent = response.Content;
                    String responseString = responseContent.ReadAsStringAsync().Result;
                    JObject caseSearch = JObject.Parse(responseString);
                    caseDetails.customerName = (String)caseSearch.SelectToken("values.customer_name");
                    caseDetails.staffResponse = (String)caseSearch.SelectToken("values.staff_response");
                    caseDetails.customerEmail = (String)caseSearch.SelectToken("values.email");
                }
                else
                {
                    await SendFailureAsync(taskToken, "Getting case details for " + caseReference + " : " + response.StatusCode.ToString());
                    Console.WriteLine("ERROR : GetStaffResponseAsync : " + request.ToString());
                    Console.WriteLine("ERROR : GetStaffResponseAsync : " + response.StatusCode.ToString());
                }
            }
            catch (Exception error)
            {
                await SendFailureAsync(taskToken, "Getting case details for " + caseReference + " : " + error.Message);
                Console.WriteLine("ERROR : GetStaffResponseAsync : " + error.StackTrace);
            }
            return caseDetails;
        }

        private async Task<String> FormatEmailAsync(String caseReference, CaseDetails caseDetails, String taskToken)
        {
            String emailBody = "";
            IAmazonS3 client = new AmazonS3Client(bucketRegion);
            try
            {
                GetObjectRequest objectRequest = new GetObjectRequest
                {
                    BucketName = "norbert.templates",
                    Key = "email-staff-response.txt"
                };
                using (GetObjectResponse objectResponse = await client.GetObjectAsync(objectRequest))
                using (Stream responseStream = objectResponse.ResponseStream)
                using (StreamReader reader = new StreamReader(responseStream))
                {
                    emailBody = reader.ReadToEnd();
                }
                emailBody = emailBody.Replace("AAA", caseReference);
                emailBody = emailBody.Replace("CCC", HttpUtility.HtmlEncode(caseDetails.staffResponse));
                emailBody = emailBody.Replace("DDD", HttpUtility.HtmlEncode(caseDetails.customerName));
            }
            catch (Exception error)
            {
                await SendFailureAsync(taskToken, " Reading Response Template : " + error.Message);
                Console.WriteLine("ERROR : FormatEmailAsync : Reading Response Template : " + error.Message);
                Console.WriteLine("ERROR : FormatEmailAsync : " + error.StackTrace);
            }
            return emailBody;
        }

        private async Task<Boolean> SendMessageAsync(String caseReference, String taskToken, String emailBody, CaseDetails caseDetails, String emailFrom)
        {
            try
            {
                AmazonSQSClient amazonSQSClient = new AmazonSQSClient(sqsRegion);
                try
                {
                    SendMessageRequest sendMessageRequest = new SendMessageRequest();
                    sendMessageRequest.QueueUrl = Environment.GetEnvironmentVariable("sqsEmailURL");
                    sendMessageRequest.MessageBody = emailBody;
                    Dictionary<string, MessageAttributeValue> MessageAttributes = new Dictionary<string, MessageAttributeValue>();
                    MessageAttributeValue messageTypeAttribute1 = new MessageAttributeValue();
                    messageTypeAttribute1.DataType = "String";
                    messageTypeAttribute1.StringValue = caseDetails.customerName;
                    MessageAttributes.Add("Name", messageTypeAttribute1);
                    MessageAttributeValue messageTypeAttribute2 = new MessageAttributeValue();
                    messageTypeAttribute2.DataType = "String";
                    messageTypeAttribute2.StringValue = caseDetails.customerEmail;
                    MessageAttributes.Add("To", messageTypeAttribute2);
                    MessageAttributeValue messageTypeAttribute3 = new MessageAttributeValue();
                    messageTypeAttribute3.DataType = "String";
                    messageTypeAttribute3.StringValue = "Northampton Borough Council: Your Call Number is " + caseReference; ;
                    MessageAttributes.Add("Subject", messageTypeAttribute3);
                    MessageAttributeValue messageTypeAttribute4 = new MessageAttributeValue();
                    messageTypeAttribute4.DataType = "String";
                    messageTypeAttribute4.StringValue = emailFrom;
                    MessageAttributes.Add("From", messageTypeAttribute4);
                    sendMessageRequest.MessageAttributes = MessageAttributes;
                    SendMessageResponse sendMessageResponse = await amazonSQSClient.SendMessageAsync(sendMessageRequest);
                }
                catch (Exception error)
                {
                    await SendFailureAsync(taskToken, "Error sending SQS message : " + error.Message);
                    Console.WriteLine("ERROR : SendMessageAsync : Error sending SQS message : '{0}'", error.Message);
                    Console.WriteLine("ERROR : SendMessageAsync : " + error.StackTrace);
                    return false;
                }
            }
            catch (Exception error)
            {
                await SendFailureAsync(taskToken, "Error starting AmazonSQSClient : " + error.Message);
                Console.WriteLine("ERROR : SendMessageAsync :  Error starting AmazonSQSClient : '{0}'", error.Message);
                Console.WriteLine("ERROR : SendMessageAsync : " + error.StackTrace);
                return false;
            }
            return true;
        }

        private async Task SendSuccessAsync(String taskToken)
        {
            AmazonStepFunctionsClient client = new AmazonStepFunctionsClient();
            SendTaskSuccessRequest successRequest = new SendTaskSuccessRequest();
            successRequest.TaskToken = taskToken;
            Dictionary<String, String> result = new Dictionary<String, String>
            {
                { "Result"  , "Success"  },
                { "Message" , "Completed"}
            };

            string requestOutput = JsonConvert.SerializeObject(result, Formatting.Indented);
            successRequest.Output = requestOutput;
            try
            {
                await client.SendTaskSuccessAsync(successRequest);
            }
            catch (Exception error)
            {
                Console.WriteLine("ERROR : SendSuccessAsync : " + error.Message);
                Console.WriteLine("ERROR : SendSuccessAsync : " + error.StackTrace);
            }
            await Task.CompletedTask;
        }

        private async Task SendFailureAsync(String taskToken, String message)
        {
            AmazonStepFunctionsClient client = new AmazonStepFunctionsClient();
            SendTaskSuccessRequest successRequest = new SendTaskSuccessRequest();
            successRequest.TaskToken = taskToken;
            Dictionary<String, String> result = new Dictionary<String, String>
            {
                { "Result"  , "Error" },
                { "Message" , message}
            };

            string requestOutput = JsonConvert.SerializeObject(result, Formatting.Indented);
            successRequest.Output = requestOutput;
            try
            {
                await client.SendTaskSuccessAsync(successRequest);
            }
            catch (Exception error)
            {
                Console.WriteLine("ERROR : SendFailureAsync : " + error.Message);
                Console.WriteLine("ERROR : SendFailureAsync : " + error.StackTrace);
            }
            await Task.CompletedTask;
        }
    }

    class CaseDetails
    {
        public String customerName { get; set; } = "";
        public String staffResponse { get; set; } = "";
        public String customerEmail { get; set; } = "";
    }

}
