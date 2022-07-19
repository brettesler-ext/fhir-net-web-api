﻿using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Net.Sockets;
using System.Net;
using Owin;
using System.Web.Http;
using Hl7.Fhir.WebApi;
using Hl7.Fhir.Model;
using Hl7.Fhir.Rest;
using Hl7.Fhir.Serialization;
using System.Net.Http;
using System.Threading.Tasks;
using System.Threading;
using System.Linq;

namespace UnitTestWebApi
{
    [TestClass]
    public class SimpleCRUDUsingHttpClientWrapper
    {
        #region << Test prepare and cleanup >>
        private IDisposable _fhirServerController;
        public string _baseAddress;

        [TestInitialize]
        public void PrepareTests()
        {
            // Ensure that we grab an available IP port on the local workstation
            // http://stackoverflow.com/questions/9895129/how-do-i-find-an-available-port-before-bind-the-socket-with-the-endpoint
            string port = "9000";

            using (Socket sock = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp))
            {
                sock.Bind(new IPEndPoint(IPAddress.Parse("127.0.0.1"), 0)); // Pass 0 here, it means to go looking for a free port
                port = ((IPEndPoint)sock.LocalEndPoint).Port.ToString();
                sock.Close();
            }

            // Now use that randomly located port to start up a local FHIR server
            _baseAddress = "http://localhost:" + port + "/";
            _fhirServerController = Microsoft.Owin.Hosting.WebApp.Start<Hl7.DemoFileSystemFhirServer.Startup>(_baseAddress);
        }

        [TestCleanup]
        public void CleanupTests()
        {
            if (_fhirServerController != null)
                _fhirServerController.Dispose();
        }
        public static void DebugDumpOutputXml(Base fragment)
        {
            if (fragment == null)
                Console.WriteLine("(null)");
            else
            {
                var doc = System.Xml.Linq.XDocument.Parse(new FhirXmlSerializer().SerializeToString(fragment));
                Console.WriteLine(doc.ToString(System.Xml.Linq.SaveOptions.None));
            }
        }
        #endregion

        public class CorrelationIdTestHandler : DelegatingHandler
        {
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                request.Headers.Add("X-Correlation-Id", "TestMe");
                var response = await base.SendAsync(request, cancellationToken);
                Assert.AreEqual("TestMe", response.Headers.GetValues("X-Correlation-Id").FirstOrDefault());
                return response;
            }
        }

        public class ProxyDetectionTestHandler : DelegatingHandler
        {
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var response = await base.SendAsync(request, cancellationToken);
                string location = response.Headers.GetValues("Location").FirstOrDefault();
                if (!string.IsNullOrEmpty(location))
                {
                    System.Diagnostics.Trace.WriteLine($">> (Status: {response.StatusCode}) {request.Method}: {location}");
                    Assert.IsTrue(!location.StartsWith("https://demo.org/testme/"), "proxy redirect detected");
                }
                Assert.AreEqual("wild-turkey-create", response.Headers.GetValues("test").FirstOrDefault(), "Custom Response header missing");
                return response;
            }
        }

        [TestMethod]
        public async System.Threading.Tasks.Task Http_CreatePatient()
        {
            Patient p = new Patient();
            p.Name = new System.Collections.Generic.List<HumanName>();
            p.Name.Add(new HumanName().WithGiven("Grahame").AndFamily("Grieve"));
            p.BirthDate = new DateTime(1970, 3, 1).ToFhirDate(); // yes there are extensions to convert to FHIR format
            p.Active = true;
            p.ManagingOrganization = new ResourceReference("Organization/1", "Demo Org");

            Hl7.Fhir.Rest.FhirHttpClient clientFhir = new Hl7.Fhir.Rest.FhirHttpClient(_baseAddress, new CorrelationIdTestHandler(), new ProxyDetectionTestHandler());

            var result = await clientFhir.CreateAsync<Patient>(p);
            DebugDumpOutputXml(result);

            Assert.IsNotNull(result.Id, "Newly created patient should have an ID");
            Assert.IsNotNull(result.Meta, "Newly created patient should have an Meta created");
            Assert.IsNotNull(result.Meta.LastUpdated, "Newly created patient should have the creation date populated");
            Assert.IsTrue(result.Active.Value, "The patient was created as an active patient");
        }

        [TestMethod]
        public async System.Threading.Tasks.Task Http_ReadPatient()
        {
            Patient p = new Patient();
            p.Id = "pat1"; // if you support this format for the IDs (client allocated ID)
            p.Name = new System.Collections.Generic.List<HumanName>();
            p.Name.Add(new HumanName().WithGiven("Grahame").AndFamily("Grieve"));
            p.BirthDate = new DateTime(1970, 3, 1).ToFhirDate(); // yes there are extensions to convert to FHIR format
            p.Active = true;
            p.ManagingOrganization = new ResourceReference("Organization/2", "Other Org");

            Hl7.Fhir.Rest.FhirHttpClient clientFhir = new Hl7.Fhir.Rest.FhirHttpClient(_baseAddress);
            var result = await clientFhir.UpdateAsync<Patient>(p);
            DebugDumpOutputXml(result);

            Assert.IsNotNull(result.Id, "Newly created patient should have an ID");
            Assert.IsNotNull(result.Meta, "Newly created patient should have an Meta created");
            Assert.IsNotNull(result.Meta.LastUpdated, "Newly created patient should have the creation date populated");
            Assert.IsTrue(result.Active.Value, "The patient was created as an active patient");

            // read the record to check that it can be loaded
            result = await clientFhir.ReadAsync<Patient>("Patient/pat1");
            Assert.AreEqual(p.Id, result.Id, "Newly created patient should have an ID");
            Assert.IsNotNull(result.Meta, "Newly created patient should have an Meta created");
            Assert.IsNotNull(result.Meta.LastUpdated, "Newly created patient should have the creation date populated");
            Assert.IsTrue(result.Active.Value, "The patient was created as an active patient");

            try
            {
                var p4 = await clientFhir.ReadAsync<Patient>("Patient/missing-client-id");
                Assert.Fail("Should have received an exception running this");
            }
            catch (Hl7.Fhir.Rest.FhirOperationException ex)
            {
                // This was the expected outcome
                System.Diagnostics.Trace.WriteLine(ex.Message);
                DebugDumpOutputXml(ex.Outcome);
            }
        }

        [TestMethod]
        public async System.Threading.Tasks.Task Http_GetPatient()
        {
            Patient p = new Patient();
            p.Id = "pat1"; // if you support this format for the IDs (client allocated ID)
            p.Name = new System.Collections.Generic.List<HumanName>();
            p.Name.Add(new HumanName().WithGiven("Grahame").AndFamily("Grieve"));
            p.BirthDate = new DateTime(1970, 3, 1).ToFhirDate(); // yes there are extensions to convert to FHIR format
            p.Active = true;
            p.ManagingOrganization = new ResourceReference("Organization/2", "Other Org");

            var clientFhir = new Hl7.Fhir.Rest.FhirHttpClient(_baseAddress);
            var result = await clientFhir.UpdateAsync<Patient>(p);
            DebugDumpOutputXml(result);

            Assert.IsNotNull(result.Id, "Newly created patient should have an ID");
            Assert.IsNotNull(result.Meta, "Newly created patient should have an Meta created");
            Assert.IsNotNull(result.Meta.LastUpdated, "Newly created patient should have the creation date populated");
            Assert.IsTrue(result.Active.Value, "The patient was created as an active patient");

            // read the record to check that it can be loaded
            result = await clientFhir.GetAsync("Patient/pat1") as Patient;
            Assert.AreEqual(p.Id, result.Id, "Newly created patient should have an ID");
            Assert.IsNotNull(result.Meta, "Newly created patient should have an Meta created");
            Assert.IsNotNull(result.Meta.LastUpdated, "Newly created patient should have the creation date populated");
            Assert.IsTrue(result.Active.Value, "The patient was created as an active patient");

            try
            {
                var p4 = await clientFhir.GetAsync("Patient/missing-client-id");
                Assert.Fail("Should have received an exception running this");
            }
            catch (Hl7.Fhir.Rest.FhirOperationException ex)
            {
                // This was the expected outcome
                System.Diagnostics.Trace.WriteLine(ex.Message);
                DebugDumpOutputXml(ex.Outcome);
            }
        }

        [TestMethod]
        public async System.Threading.Tasks.Task Http_UpdatePatient()
        {
            Patient p = new Patient();
            p.Id = "pat1"; // if you support this format for the IDs (client allocated ID)
            p.Name = new System.Collections.Generic.List<HumanName>();
            p.Name.Add(new HumanName().WithGiven("Grahame").AndFamily("Grieve"));
            p.BirthDate = new DateTime(1970, 3, 1).ToFhirDate(); // yes there are extensions to convert to FHIR format
            p.Active = true;
            p.ManagingOrganization = new ResourceReference("Organization/2", "Other Org");

            Hl7.Fhir.Rest.FhirHttpClient clientFhir = new Hl7.Fhir.Rest.FhirHttpClient(_baseAddress);
            var result = await clientFhir.UpdateAsync<Patient>(p);
            DebugDumpOutputXml(result);

            Assert.IsNotNull(result.Id, "Newly created patient should have an ID");
            Assert.IsNotNull(result.Meta, "Newly created patient should have an Meta created");
            Assert.IsNotNull(result.Meta.LastUpdated, "Newly created patient should have the creation date populated");
            Assert.IsTrue(result.Active.Value, "The patient was created as an active patient");
        }

        [TestMethod]
        public async System.Threading.Tasks.Task Http_DeletePatient_resource_signature()
        {
            Patient p = new Patient();
            p.Name = new System.Collections.Generic.List<HumanName>();
            p.Name.Add(new HumanName().WithGiven("Grahame").AndFamily("Grieve"));
            p.BirthDate = new DateTime(1970, 3, 1).ToFhirDate(); // yes there are extensions to convert to FHIR format
            p.Active = true;
            p.ManagingOrganization = new ResourceReference("Organization/1", "Demo Org");

            Hl7.Fhir.Rest.FhirHttpClient clientFhir = new Hl7.Fhir.Rest.FhirHttpClient(_baseAddress);
            var result = await clientFhir.CreateAsync<Patient>(p);
            DebugDumpOutputXml(result);

            Assert.IsNotNull(result.Id, "Newly created patient should have an ID");
            Assert.IsNotNull(result.Meta, "Newly created patient should have an Meta created");
            Assert.IsNotNull(result.Meta.LastUpdated, "Newly created patient should have the creation date populated");
            Assert.IsTrue(result.Active.Value, "The patient was created as an active patient");

            // Now delete the patient we just created
            await clientFhir.DeleteAsync(result);

            try
            {
                var p4 = await clientFhir.ReadAsync<Patient>($"Patient/{result.Id}");
                Assert.Fail("Should have received an exception running this");
            }
            catch (Hl7.Fhir.Rest.FhirOperationException ex)
            {
                Assert.AreEqual(HttpStatusCode.Gone, ex.Status, "Expected the patient to have been deleted");
                // This was the expected outcome
                System.Diagnostics.Trace.WriteLine(ex.Message);
                DebugDumpOutputXml(ex.Outcome);
            }
        }

        [TestMethod]
        public async System.Threading.Tasks.Task Http_DeletePatient_string_signature()
        {
            var p = new Patient
            {
                Name = new System.Collections.Generic.List<HumanName>
                {
                    new HumanName().WithGiven("Grahame").AndFamily("Grieve")
                },
                BirthDate = new DateTime(1970, 3, 1).ToFhirDate(),
                Active = true,
                ManagingOrganization = new ResourceReference("Organization/1", "Demo Org")
            };
            // yes there are extensions to convert to FHIR format

            var clientFhir = new FhirHttpClient(_baseAddress);
            var result = await clientFhir.CreateAsync<Patient>(p);
            DebugDumpOutputXml(result);

            Assert.IsNotNull(result.Id, "Newly created patient should have an ID");
            Assert.IsNotNull(result.Meta, "Newly created patient should have an Meta created");
            Assert.IsNotNull(result.Meta.LastUpdated, "Newly created patient should have the creation date populated");
            Assert.IsTrue(result.Active.Value, "The patient was created as an active patient");

            // Now delete the patient we just created
            await clientFhir.DeleteAsync($"Patient/{result.Id}");

            try
            {
                var p4 = await clientFhir.ReadAsync<Patient>($"Patient/{result.Id}");
                Assert.Fail("Should have received an exception running this");
            }
            catch (FhirOperationException ex)
            {
                Assert.AreEqual(HttpStatusCode.Gone, ex.Status, "Expected the patient to have been deleted");
                // This was the expected outcome
                System.Diagnostics.Trace.WriteLine(ex.Message);
                DebugDumpOutputXml(ex.Outcome);
            }
        }

        [TestMethod]
        public async System.Threading.Tasks.Task Http_CapabilityStatement()
        {
            Hl7.Fhir.Rest.FhirHttpClient clientFhir = new Hl7.Fhir.Rest.FhirHttpClient(_baseAddress);
            var result = await clientFhir.CapabilityStatementAsync();
            DebugDumpOutputXml(result);

            Assert.IsInstanceOfType(result, typeof(CapabilityStatement));
            Assert.IsNull(result.Id, "Live Server Capability Statements don't typically have an ID");
            Assert.IsNotNull(result.Meta, "Newly created patient should have an Meta created");
            Assert.IsNotNull(result.Meta.LastUpdated, "Newly created patient should have the creation date populated");
        }

        [TestMethod]
        public async System.Threading.Tasks.Task Http_ValidatePatientOnAzure()
        {
            Patient p = new Patient();
            p.Name = new System.Collections.Generic.List<HumanName>();
            p.Name.Add(new HumanName().WithGiven("Grahame").AndFamily("Grieve"));
            p.BirthDate = "123-123-123";
            p.Active = true;
            p.ManagingOrganization = new ResourceReference("Organization/1", "Demo Org");

            var clientFhir = new Hl7.Fhir.Rest.FhirHttpClient("https://sqlonfhir-r4.azurewebsites.net/fhir");
            try
            {
                var result = await clientFhir.ValidateResourceAsync(p);
                DebugDumpOutputXml(result);
                Assert.Fail("expected it to throw");
            }
            catch(FhirOperationException ex)
            {
                DebugDumpOutputXml(ex.Outcome);
                Assert.AreEqual(HttpStatusCode.BadRequest, ex.Status);
                Assert.AreEqual(OperationOutcome.IssueSeverity.Error, ex.Outcome.Issue.FirstOrDefault().Severity);
                Assert.AreEqual("Body parsing failed: Type checking the data: Literal '123-123-123' cannot be interpreted as a date: 'Partial is in an invalid format, should use ISO8601 YYYY-MM-DDThh:mm:ss+TZ notation'. (at Parameters.parameter[0].resource[0].birthDate[0])", ex.Outcome.Issue.FirstOrDefault().Details.Text);
            }

            p.BirthDate = "1970-01-01";
            var resultGood = await clientFhir.ValidateResourceAsync(p);
            DebugDumpOutputXml(resultGood);
        }

        [TestMethod, Ignore]
        public async System.Threading.Tasks.Task Http_SearchAzure()
        {
            Hl7.Fhir.Rest.FhirHttpClient clientFhir = new Hl7.Fhir.Rest.FhirHttpClient("https://sqlonfhir-r4.azurewebsites.net/fhir");
            var result = await clientFhir.SearchAsync<Patient>(new[] { "name=Kaitlin" });
            DebugDumpOutputXml(result);
            Assert.IsInstanceOfType(result, typeof(Bundle));
            Assert.AreEqual(1, result.Total.Value, "Expect only 1 Kaitlin");

            result = await clientFhir.SearchAsync<Patient>(new[] { "name=k" });
            DebugDumpOutputXml(result);
            Assert.IsInstanceOfType(result, typeof(Bundle));
            Assert.AreEqual(2, result.Total.Value, "Expect 2 k patients");
        }

        [TestMethod, Ignore]
        public async System.Threading.Tasks.Task Http_SearchAzureNoParams()
        {
            Hl7.Fhir.Rest.FhirHttpClient clientFhir = new Hl7.Fhir.Rest.FhirHttpClient("https://sqlonfhir-r4.azurewebsites.net/fhir");
            var result = await clientFhir.SearchAsync<Patient>();
            DebugDumpOutputXml(result);
            Assert.IsInstanceOfType(result, typeof(Bundle));
            Assert.AreEqual(32, result.Total.Value, "volume on test server");
        }

        [TestMethod, Ignore]
        public async System.Threading.Tasks.Task Http_SearchAzureWithSearchParams()
        {
            Hl7.Fhir.Rest.FhirHttpClient clientFhir = new Hl7.Fhir.Rest.FhirHttpClient("https://sqlonfhir-r4.azurewebsites.net/fhir");
            var sp = new SearchParams();
            sp.Add("name", "Kaitlin");
            var result = await clientFhir.SearchAsync<Patient>(sp);
            DebugDumpOutputXml(result);
            Assert.IsInstanceOfType(result, typeof(Bundle));
            Assert.AreEqual(1, result.Total.Value, "Expect only 1 Kaitlin");

            sp = new SearchParams();
            sp.Add("name", "k");
            result = await clientFhir.SearchAsync<Patient>(sp);
            DebugDumpOutputXml(result);
            Assert.IsInstanceOfType(result, typeof(Bundle));
            Assert.AreEqual(2, result.Total.Value, "Expect 2 ks");
        }

        [TestMethod, Ignore]
        public async System.Threading.Tasks.Task Http_SearchContinueAzure()
        {
            var clientFhir = new Hl7.Fhir.Rest.FhirHttpClient("https://sqlonfhir-r4.azurewebsites.net/fhir");
            var result = await clientFhir.SearchAsync<Patient>();
            DebugDumpOutputXml(result);
            int nPages = 0;
            int nTotal = 0;
            int nTotalBundleReport = result.Total.Value;
            Assert.IsInstanceOfType(result, typeof(Bundle));
            while (result != null)
            {
                nTotal += result.Entry.Count; 
                nPages++;
                result = await clientFhir.ContinueAsync(result);
            }
            Assert.AreEqual(32, nTotal, "entry count incorrect");
            Assert.AreEqual(32, nTotalBundleReport, "server calculation of count incorrect");
            Assert.AreEqual(2, nPages);
        }

        //[TestMethod]
        //public void Http_PerformCustomOperationCountResourceTypeInstances()
        //{
        //    Hl7.Fhir.Rest.FhirHttpClient clientFhir = new Hl7.Fhir.Rest.FhirHttpClient(_baseAddress, false);
        //    var result = clientFhir.TypeOperation<Patient>("count-em", null, true) as OperationOutcome;
        //    DebugDumpOutputXml(result);
        //    Assert.IsNotNull(result, "Should be a capability statement returned");
        //    Assert.AreEqual(1, result.Issue.Count, "Should contain the issue that has the count of the number of resources in there");
        //    Console.WriteLine($"{result.Issue[0].Details.Text}");
        //}

        //[TestMethod]
        //public void PerformCustomOperationWithIdParameter()
        //{
        //    Hl7.Fhir.Rest.FhirHttpClient clientFhir = new Hl7.Fhir.Rest.FhirHttpClient(_baseAddress, false);
        //    clientFhir.OnBeforeRequest += ClientFhir_OnBeforeRequest;
        //    string exampleQuery = $"{_baseAddress}NamingSystem/$preferred-id?id=45&type=uri";
        //    var result = clientFhir.Get(exampleQuery) as NamingSystem;
        //    DebugDumpOutputXml(result);
        //    Assert.IsNotNull(result, "Should be a NamingSystem returned");
        //    Assert.AreEqual("45", result.Id);
        //}

        [TestMethod]
        public async System.Threading.Tasks.Task Http_PerformCustomOperation()
        {
            Hl7.Fhir.Rest.FhirHttpClient clientFhir = new Hl7.Fhir.Rest.FhirHttpClient(_baseAddress);
            var exampleQuery = new Uri($"{_baseAddress}Patient/45");
            var result = await clientFhir.InstanceOperationAsync(exampleQuery, "send-activation-code") as OperationOutcome;
            DebugDumpOutputXml(result);
            Assert.IsNotNull(result, "Should be a OperationOutcome returned");
            Assert.AreEqual("Send an activation code to Patient/45", result.Issue.FirstOrDefault()?.Details.Text);

            exampleQuery = new Uri("Patient/45", UriKind.RelativeOrAbsolute);
            result = await clientFhir.InstanceOperationAsync(exampleQuery, "send-activation-code") as OperationOutcome;
            DebugDumpOutputXml(result);
            Assert.IsNotNull(result, "Should be a OperationOutcome returned");
            Assert.AreEqual("Send an activation code to Patient/45", result.Issue.FirstOrDefault()?.Details.Text);
        }

        [TestMethod]
        public async System.Threading.Tasks.Task Http_PerformTypeOperation()
        {
            Hl7.Fhir.Rest.FhirHttpClient clientFhir = new Hl7.Fhir.Rest.FhirHttpClient(_baseAddress);
            var result = await clientFhir.TypeOperationAsync("count-em", "Patient") as OperationOutcome;
            DebugDumpOutputXml(result);
            Assert.IsNotNull(result, "Should be a OperationOutcome returned");
            Assert.IsTrue(result.Issue.FirstOrDefault()?.Details.Text.StartsWith("Patient resource instances: ") == true);
        }
    }
}
