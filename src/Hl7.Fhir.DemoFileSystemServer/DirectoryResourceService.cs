﻿using System;
using System.Collections.Generic;
using Hl7.Fhir.Model;
using Hl7.Fhir.Rest;
using Hl7.Fhir.WebApi;
using Hl7.Fhir.Utility;
using System.Threading.Tasks;
using System.Linq;
using Hl7.Fhir.Support;
using System.IO;
using Hl7.Fhir.ElementModel;
using Hl7.Fhir.Language.Debugging;
using System.Net;
using Hl7.Fhir.Specification.Source;
using Hl7.Fhir.Specification.Terminology;

namespace Hl7.Fhir.DemoFileSystemFhirServer
{
    public class DirectoryResourceService<TSP> : Hl7.Fhir.WebApi.IFhirResourceServiceR4<TSP>
        where TSP : class
    {
        public ModelBaseInputs<TSP> RequestDetails { get; private set; }

        public string ResourceName { get; private set; }
        public string ResourceDirectory { get; private set; }
        public SearchIndexer Indexer { get; set; }

        public IResourceResolver Source { get; private set; }
        public IAsyncResourceResolver AsyncSource { get; private set; }

        public DirectoryResourceService(ModelBaseInputs<TSP> requestDetails, string resourceName, string directory, IResourceResolver source, IAsyncResourceResolver asyncSource)
        {
            this.RequestDetails = requestDetails;
            this.ResourceDirectory = directory;
            this.ResourceName = resourceName;
            this.Source = source;
            this.AsyncSource = asyncSource;
        }

        public DirectoryResourceService(ModelBaseInputs<TSP> requestDetails, string resourceName, string directory, IResourceResolver Source, IAsyncResourceResolver AsyncSource, SearchIndexer indexer)
        {
            this.RequestDetails = requestDetails;
            this.ResourceDirectory = directory;
            this.ResourceName = resourceName;
            this.Source = Source;
            this.AsyncSource = AsyncSource;
            this.Indexer = indexer;
        }


        protected static Serialization.FhirXmlSerializer _serializer = new Serialization.FhirXmlSerializer(new Serialization.SerializerSettings() { Pretty = true });
        protected static Serialization.FhirXmlParser _parser = new Serialization.FhirXmlParser();

        virtual public async Task<Resource> Create(Resource resource, string ifMatch, string ifNoneExist, DateTimeOffset? ifModifiedSince)
        {
#if DEBUG
            RequestDetails.SetResponseHeaderValue("test", "wild-turkey-create");
#endif

            if (String.IsNullOrEmpty(resource.Id))
                resource.Id = Guid.NewGuid().ToFhirId();
            if (resource.Meta == null)
                resource.Meta = new Meta();
            resource.Meta.LastUpdated = DateTime.Now;

            // Get the latest version number
            int versionNumber = 1; // default will be 1
            foreach (var file in Directory.EnumerateFiles(ResourceDirectory, $"{resource.TypeName}.{resource.Id}.*.xml"))
            {
                string versionSection = file.Substring(0, file.LastIndexOf('.'));
                versionSection = versionSection.Substring(versionSection.LastIndexOf('.') + 1);
                int verFile;
                if (int.TryParse(versionSection, out verFile))
                {
                    if (verFile >= versionNumber)
                        versionNumber = verFile + 1;
                }
            }
            resource.Meta.VersionId = versionNumber.ToString();
            string path = Path.Combine(ResourceDirectory, $"{resource.TypeName}.{resource.Id}.{resource.Meta.VersionId}.xml");

            // Validate the resource before we actually store it!
            var validationMode = ResourceValidationMode.create;
            if (versionNumber != 1)
                validationMode = ResourceValidationMode.update;
            var validationOutcome = await ValidateResource(resource, validationMode, null);
            if (!validationOutcome.Success)
            {
                var message = $"Validation failed: {validationOutcome.Errors} errors, {validationOutcome.Warnings}";
                if (validationOutcome.Fatals > 0)
                    message += $" ({validationOutcome.Fatals} fatals)";

                // Temporarily remove the warnings/information messages
                validationOutcome.Issue.RemoveAll(i => i.Severity == OperationOutcome.IssueSeverity.Warning);
                validationOutcome.Issue.RemoveAll(i => i.Severity == OperationOutcome.IssueSeverity.Information);

                throw new FhirServerException(System.Net.HttpStatusCode.BadRequest, validationOutcome, $"Validation failed: {validationOutcome.Errors} errors, {validationOutcome.Warnings} ({validationOutcome.Fatals} fatals)");
            }

            var settings = new Serialization.SerializerSettings() { Pretty = true };
            File.WriteAllText(
                path,
                _serializer.SerializeToString(resource));
            path = Path.Combine(ResourceDirectory, $"{resource.TypeName}.{resource.Id}..xml"); // the current version of the resource
            File.WriteAllText(
                path,
                _serializer.SerializeToString(resource));
            if (validationMode == ResourceValidationMode.create)
                resource.SetAnnotation<CreateOrUpate>(CreateOrUpate.Create);
            else
                resource.SetAnnotation<CreateOrUpate>(CreateOrUpate.Update);
            // and update the search index
            Indexer.ScanResource(resource, Path.Combine(ResourceDirectory, $"{resource.TypeName}.{resource.Id}..xml"));
            return resource;
        }

        public enum ResourceValidationMode { create, update, delete, profile };
        virtual public Task<OperationOutcome> ValidateResource(Resource resource, ResourceValidationMode mode, string[] profiles)
        {
			// https://github.com/FirelyTeam/firely-docs-firely-net-sdk/blob/f8c9271c21636fdfd21942485a7fa0032545f5ef/validation/terminology-service.rst
			var localTermService = new LocalTerminologyService(AsyncSource, new ValueSetExpanderSettings() { MaxExpansionSize = 1500 });
			var mimeTypeTermService = new MimeTypeTerminologyService();
            // var tsClient = new FhirClient("https://r4.ontoserver.csiro.au/fhir");
            // var externalTermService = new ExternalTerminologyService(tsClient);
            var multiTermService = new MultiTerminologyService(localTermService, mimeTypeTermService); //, externalTermService);
            var compiler = new Hl7.FhirPath.FhirPathCompiler();
            var settings = new Hl7.Fhir.Validation.ValidationSettings()
            {
                ResourceResolver = Source,
                TerminologyService = multiTermService,
                FhirPathCompiler = compiler
            };

            //settings.ConstraintsToIgnore = settings.ConstraintsToIgnore.Union(new []{
            //    "ref-1", // causes issues with
            //    // "ctm-1", // should permit prac roles too
            //    // "sdf-0" // name properties should be usable as identifiers in code (no spaces etc)

            //} ).Distinct().ToArray();
            var validator = new Hl7.Fhir.Validation.Validator(settings);
            var outcome = validator.Validate(resource.ToTypedElement(), profiles);

            // strip out any profile missing
            outcome.Issue.RemoveAll((i) => i.Details.Coding.Any(c => c.Code == "4000") && i.Details.Text.Contains("Unable to resolve reference to profile"));

            // Version 1.9.0 of the core libs incorrectly report the errors in location, not expression, so move them over
            foreach (var issue in outcome.Issue)
            {
                if (!issue.ExpressionElement.Any())
                {
                    issue.ExpressionElement = issue.LocationElement;
                    issue.LocationElement = null;
                }
            }

            // If there is an annotation from the parser with the outcome from parsing, inject that too.
            if (resource?.HasAnnotation<OperationOutcome>() == true)
            {
                outcome.Issue.AddRange(resource.Annotation<OperationOutcome>().Issue);
            }

            // If the resource has the subsetted meta tag, then reject the create/update
            if (resource?.Meta?.Tag.Any(m => m.System == ResourceExtensions.SubsettedSystem && m.Code == "SUBSETTED" 
                                            || m.System == ResourceExtensions.SubsettedSystem && m.Code == "SUBSETTED") == true)
            {
				outcome.Issue.Add(new OperationOutcome.IssueComponent()
				{
					Severity = OperationOutcome.IssueSeverity.Error,
					Code = OperationOutcome.IssueType.BusinessRule,
					Details = new CodeableConcept() { Text = $"Cannot create/update a resource that is subsetted" }
				});
			}

            return Task<OperationOutcome>.FromResult(outcome);
        }

        virtual public Task<string> Delete(string id, string ifMatch)
        {
            string path = Path.Combine(ResourceDirectory, $"{this.ResourceName}.{id}..xml");
            if (File.Exists(path))
                File.Delete(path);
            return System.Threading.Tasks.Task.FromResult<string>(null);
        }

        virtual public Task<Resource> Get(string resourceId, string VersionId, SummaryType summary)
        {
            RequestDetails.SetResponseHeaderValue("test", "wild-turkey-get");

            string path = Path.Combine(ResourceDirectory, $"{this.ResourceName}.{resourceId}.{VersionId}.xml");
            if (File.Exists(path))
                return System.Threading.Tasks.Task.FromResult(_parser.Parse<Resource>(File.ReadAllText(path)));
            throw new FhirServerException(System.Net.HttpStatusCode.Gone, "It might have been deleted!");
        }

        virtual public Task<CapabilityStatement.ResourceComponent> GetRestResourceComponent()
        {
            var rt = new Hl7.Fhir.Model.CapabilityStatement.ResourceComponent();
            rt.TypeElement = new() { ObjectValue = ResourceName };
            rt.ReadHistory = true;
            rt.UpdateCreate = true;
            rt.Versioning = CapabilityStatement.ResourceVersionPolicy.Versioned;
            rt.ConditionalCreate = false;
            rt.ConditionalUpdate = false;
            rt.ConditionalDelete = CapabilityStatement.ConditionalDeleteStatus.NotSupported;

            rt.Interaction = new List<CapabilityStatement.ResourceInteractionComponent>()
            {
                new CapabilityStatement.ResourceInteractionComponent() { Code = CapabilityStatement.TypeRestfulInteraction.Create },
                new CapabilityStatement.ResourceInteractionComponent() { Code = CapabilityStatement.TypeRestfulInteraction.Read },
                new CapabilityStatement.ResourceInteractionComponent() { Code = CapabilityStatement.TypeRestfulInteraction.Update },
                new CapabilityStatement.ResourceInteractionComponent() { Code = CapabilityStatement.TypeRestfulInteraction.Delete },
                new CapabilityStatement.ResourceInteractionComponent() { Code = CapabilityStatement.TypeRestfulInteraction.SearchType },
                //new CapabilityStatement.ResourceInteractionComponent() { Code = CapabilityStatement.TypeRestfulInteraction.Vread },
                //new CapabilityStatement.ResourceInteractionComponent() { Code = CapabilityStatement.TypeRestfulInteraction.HistoryInstance },
                //new CapabilityStatement.ResourceInteractionComponent() { Code = CapabilityStatement.TypeRestfulInteraction.HistoryType },
            };

            return System.Threading.Tasks.Task.FromResult(rt);
        }

        virtual public Task<Bundle> InstanceHistory(string ResourceId, DateTimeOffset? since, DateTimeOffset? Till, int? Count, SummaryType summary)
        {
            Bundle result = new Bundle();
            result.Meta = new Meta()
            {
                LastUpdated = DateTime.Now
            };
            result.Id = new Uri("urn:uuid:" + Guid.NewGuid().ToString("n")).OriginalString;
            result.Type = Bundle.BundleType.History;

            var files = Directory.EnumerateFiles(ResourceDirectory, $"{ResourceName}.{ResourceId}.?*.xml");
            foreach (var filename in files)
            {
                if (filename.EndsWith("..xml"))
                    continue;
                var resource = _parser.Parse<Resource>(File.ReadAllText(filename));
                result.AddResourceEntry(resource,
                    ResourceIdentity.Build(RequestDetails.BaseUri,
                        resource.TypeName,
                        resource.Id,
                        resource.Meta.VersionId).OriginalString);
            }
            result.Total = result.Entry.Count;
            result.Entry.Sort((x, y) => { return y.Resource.Meta.VersionId.CompareTo(x.Resource.Meta.VersionId); });

            // also need to set the page links

            return System.Threading.Tasks.Task.FromResult(result);
        }

        virtual public async Task<Resource> PerformOperation(string operation, Parameters operationParameters, SummaryType summary)
        {
            switch (operation.ToLower())
            {
                case "validate":
                    return await PerformOperation_Validate(operationParameters, summary);
            }
            if (operation == "count-em")
            {
                var result = new OperationOutcome();
                result.Issue.Add(new OperationOutcome.IssueComponent()
                {
                    Code = OperationOutcome.IssueType.Informational,
                    Severity = OperationOutcome.IssueSeverity.Information,
                    Details = new CodeableConcept(null, null, $"{ResourceName} resource instances: {Directory.EnumerateFiles(ResourceDirectory, $"{ResourceName}.*.xml").Count()}")
                });
                return result;
            }
          
            if (operation == "preferred-id")
            {
                // Test operation that isn't really anything just for a specific unit test
                NamingSystem ns = new NamingSystem() { Id = operationParameters.GetString("id") };
                return ns;
            }

            throw new NotImplementedException();
        }

        virtual public async Task<Resource> PerformOperation(string id, string operation, Parameters operationParameters, SummaryType summary)
        {
            switch (operation.ToLower())
            {
                case "validate":
                    if (operationParameters.Parameter.Any(p => p.Name.ToLower() == "resource"))
                    {
                        var outcome = new OperationOutcome();
                        outcome.Issue.Add(new OperationOutcome.IssueComponent()
                        {
                            Code = OperationOutcome.IssueType.Incomplete,
                            Severity = OperationOutcome.IssueSeverity.Error,
                            Details = new CodeableConcept(null, null, "When calling the resource instance validate operation the 'resource' parameters must not be provided")
                        });
                        outcome.SetAnnotation<HttpStatusCode>(HttpStatusCode.BadRequest);
                        return outcome;
                    }
                    var resource = await Get(id, null, SummaryType.False);
                    operationParameters.Add("resource", resource);
                    return await PerformOperation_Validate(operationParameters, summary);
            }
            if (operation == "send-activation-code")
            {
                // Test operation that isn't really anything just for a specific unit test
                OperationOutcome outcome = new OperationOutcome() { Id = operationParameters?.GetString("id") };
                outcome.Issue.Add(new OperationOutcome.IssueComponent
                {
                    Severity = OperationOutcome.IssueSeverity.Information,
                    Code = OperationOutcome.IssueType.Informational,
                    Details = new CodeableConcept() { Text = $"Send an activation code to {ResourceName}/{id}" }
                });
                return outcome;
            }
            if (operation == "summary")
            {
                var patient = await Get(id, null, SummaryType.False);

                List<KeyValuePair<string, string>> paramv = new List<KeyValuePair<string, string>>();
                paramv.Add(new KeyValuePair<string, string>("patient", id));
                paramv.Add(new KeyValuePair<string, string>("category", "problem-list-item"));

                var problems = await Search("Condition", paramv, null, SummaryType.False, null);
                problems.Entry.ForEach(x => { x.Search=null; ((Condition)x.Resource).Recorder = null; ((Condition)x.Resource).Asserter = null; } );

                paramv = new List<KeyValuePair<string, string>>();
                paramv.Add(new KeyValuePair<string, string>("patient", id));
                var procedures = await Search("Procedure", paramv, null, SummaryType.False, null);
                procedures.Entry.ForEach(x => { x.Search=null; ((Procedure)x.Resource).Recorder = null; ((Procedure)x.Resource).Asserter = null; });

                paramv = new List<KeyValuePair<string, string>>();
                paramv.Add(new KeyValuePair<string, string>("patient", id));
                var allergies = await Search("AllergyIntolerance", paramv, null, SummaryType.False, null);
                allergies.Entry.ForEach(x => { x.Search=null; ((AllergyIntolerance)x.Resource).Recorder = null; ((AllergyIntolerance)x.Resource).Asserter = null; });

                paramv = new List<KeyValuePair<string, string>>();
                paramv.Add(new KeyValuePair<string, string>("patient", id));
                var medications = await Search("MedicationRequest", paramv, null, SummaryType.False, null);
                medications.Entry.ForEach(x => { x.Search=null; ((MedicationRequest)x.Resource).Recorder = null; });

                paramv = new List<KeyValuePair<string, string>>();
                paramv.Add(new KeyValuePair<string, string>("patient", id));
                var observations = await Search("Observation", paramv, null, SummaryType.False, null);
                observations.Entry.ForEach(x => { x.Search=null; });
               
                var results_observations = observations.Entry.Where((x) => {
                    var o = ((Observation)x.Resource);
                    return (o.Category != null &&o.Category.Exists(c => c.Coding.Exists(cc => cc.Code == "laboratory")));
                }).ToList();

                var vitals_observations = observations.Entry.Where((x) => {
                    var o = ((Observation)x.Resource);
                    return (o.Category != null && o.Category.Exists(c => c.Coding.Exists(cc => cc.Code == "vital-signs")));
                }).ToList();

                var socialhx_observations = observations.Entry.Where((x) => {
                    var o = ((Observation)x.Resource);
                    return (o.Code != null && o.Code.Coding.Any() && o.Code.Coding.Exists(cc => cc.Code == " 72166-2"));
                }).ToList();

                Bundle b = new Bundle()
                {
                    Id = System.Guid.NewGuid().ToString(),
                };

                var problems_section = new Composition.SectionComponent()
                {
                    Title = "Active Problems",
                    Code = new CodeableConcept("11450-4", "http://loinc.org"),
                    Entry = problems.Entry.Select(x => new ResourceReference() { Reference = "Condition/" + x.Resource.Id }).ToList()

                };

                var procedures_section = new Composition.SectionComponent()
                {
                    Title = "History of Procedures Section",
                    Code = new CodeableConcept("47519-4", "http://loinc.org"),
                    Entry = procedures.Entry.Select(x => new ResourceReference() { Reference = "Procedure/" + x.Resource.Id }).ToList()

                };
          
                var allergies_section = new Composition.SectionComponent()
                {
                    Title = "Allergies and Intolerances",
                    Code = new CodeableConcept("48765-2", "http://loinc.org"),
                    Entry = allergies.Entry.Select(x => new ResourceReference() { Reference = "AllergyIntolerance/" + x.Resource.Id }).ToList()

                };

                var medications_section = new Composition.SectionComponent()
                {
                    Title = "Medication Summary section",
                    Code = new CodeableConcept("10160-0", "http://loinc.org"),
                    Entry = medications.Entry.Select(x => new ResourceReference() { Reference = "MedicationRequest/" + x.Resource.Id }).ToList()

                };

                var results_section = new Composition.SectionComponent()
                {
                    Title = "Results Section",
                    Code = new CodeableConcept("30954-2", "http://loinc.org"),
                    Entry = results_observations.Select(x => new ResourceReference() { Reference = "Observation/" + x.Resource.Id }).ToList()

                };

                var vitals_section = new Composition.SectionComponent()
                {
                    Title = "Vital Signs Section",
                    Code = new CodeableConcept("8716-3", "http://loinc.org"),
                    Entry = vitals_observations.Select(x => new ResourceReference() { Reference = "Observation/" + x.Resource.Id }).ToList()

                };

                var socialhx_section = new Composition.SectionComponent()
                {
                    Title = "Social History Section",
                    Code = new CodeableConcept("29762-2", "http://loinc.org"),
                    Entry = socialhx_observations.Select(x => new ResourceReference() { Reference = "Observation/" + x.Resource.Id }).ToList()

                };

                Composition c = new Composition()
                {
                    Id = System.Guid.NewGuid().ToString(),
                    Type = new CodeableConcept("60591-5", "http://loinc.org"), 
                    Status = CompositionStatus.Final,
                    Text = new Narrative("<p>AUPS</p>"),
                    DateElement =  new FhirDateTime(DateTimeOffset.Now),
                    Title = "AU Patient Summary",
                    Attester = new List<Composition.AttesterComponent>()
                    {
                        new Composition.AttesterComponent()
                        {
                            Mode = Composition.CompositionAttestationMode.Personal,
                            TimeElement = new FhirDateTime(DateTimeOffset.Now),
                            Party = new ResourceReference("Patient/" + patient.Id)
                        }
                    },
                    Section = new List<Composition.SectionComponent>() {
                       
                    }
          
                };


                if (problems.Entry.Any())
                    c.Section.Add(problems_section);

                if (procedures.Entry.Any())
                    c.Section.Add(procedures_section);

                if (allergies.Entry.Any())
                    c.Section.Add(allergies_section);

                if (medications.Entry.Any())
                    c.Section.Add(medications_section);

                if (results_observations.Any())
                    c.Section.Add(results_section);

                if (vitals_observations.Any())
                    c.Section.Add(vitals_section);

                if (socialhx_observations.Any())
                    c.Section.Add(socialhx_section);


                b.Entry.Add(new Bundle.EntryComponent() { FullUrl="uuid:" + c.Id, Resource =  c });
                b.Entry.Add(new Bundle.EntryComponent() { Resource =  patient });
                b.Entry.AddRange(problems.Entry);
                b.Entry.AddRange(allergies.Entry);
                b.Entry.AddRange(medications.Entry);

                return b;
            }
            throw new NotImplementedException();
        }

        protected async Task<Resource> PerformOperation_Validate(Parameters operationParameters, SummaryType summary)
        {
            var outcome = new OperationOutcome();
            ResourceValidationMode? mode = ResourceValidationMode.create;
            Resource resource = operationParameters["resource"]?.Resource;
            List<string> profiles = new List<string>();

            var modeParams = operationParameters.Parameter.Where(p => p.Name?.ToLower() == "mode");
            if (modeParams.Count() > 1)
            {
                outcome.Issue.Add(new OperationOutcome.IssueComponent()
                {
                    Code = OperationOutcome.IssueType.Informational,
                    Severity = OperationOutcome.IssueSeverity.Information,
                    Details = new CodeableConcept(null, null, "Multiple 'mode' parameters provided, using the first one")
                });
            }
            if (modeParams.Any())
            {
                string modeStr = null;
                var value = modeParams.First().Value;
                if (value is Code code) modeStr = code.Value;
                else if (value is FhirString str) modeStr = str.Value;
                else
                {
                    outcome.Issue.Add(new OperationOutcome.IssueComponent()
                    {
                        Code = OperationOutcome.IssueType.Structure,
                        Severity = OperationOutcome.IssueSeverity.Error,
                        Details = new CodeableConcept(null, null, "Multiple 'mode' parameters provided, using the first one")
                    });
                }
                if (!string.IsNullOrEmpty(modeStr))
                {
                    mode = EnumUtility.ParseLiteral<ResourceValidationMode>(modeStr, true);
                    if (!mode.HasValue)
                    {
                        outcome.Issue.Add(new OperationOutcome.IssueComponent()
                        {
                            Code = OperationOutcome.IssueType.CodeInvalid,
                            Severity = OperationOutcome.IssueSeverity.Error,
                            Details = new CodeableConcept(null, null, $"Invalid 'mode' parameter value '{modeStr}'")
                        });
                    }
                }
            }

            var resourceParams = operationParameters.Parameter.Where(p => p.Name == "resource");
            if (resourceParams.Count() > 1)
            {
                outcome.Issue.Add(new OperationOutcome.IssueComponent()
                {
                    Code = OperationOutcome.IssueType.Incomplete,
                    Severity = OperationOutcome.IssueSeverity.Error,
                    Details = new CodeableConcept(null, null, "Multiple 'resource' parameters provided")
                });
            }
            if (resourceParams.Count() != 1 && mode != ResourceValidationMode.delete)
            {
                outcome.Issue.Add(new OperationOutcome.IssueComponent()
                {
                    Code = OperationOutcome.IssueType.Incomplete,
                    Severity = OperationOutcome.IssueSeverity.Error,
                    Details = new CodeableConcept(null, null, "Missing the 'resource' parameter")
                });
            }

            var profileParams = operationParameters.Parameter.Where(p => p.Name?.ToLower() == "profile");
            if (profileParams.Any())
            {
                foreach (var value in profileParams.Select(p => p.Value))
                {
                    if (value is FhirUri code)
                        profiles.Add(code.Value);
                    else if (value is FhirString str)
                        profiles.Add(str.Value);
                }
            }

            if (resource != null && resource.TypeName != this.ResourceName)
            {
                outcome.Issue.Add(new OperationOutcome.IssueComponent()
                {
                    Code = OperationOutcome.IssueType.Incomplete,
                    Severity = OperationOutcome.IssueSeverity.Error,
                    Details = new CodeableConcept(null, null, $"Cannot validate a '{resource.TypeName}' resource on the '{this.ResourceName}' endpoint")
                });
            }

            if (!outcome.Success)
            {
                outcome.SetAnnotation<HttpStatusCode>(HttpStatusCode.BadRequest);
                return outcome;
            }

            var result = await ValidateResource(resource, mode.Value, profiles.ToArray());
            outcome.Issue.AddRange(result.Issue);
            if (outcome.Success)
            {
                // resource validated fine, add an information message to report it
                string summaryMessage = $"Validation of '{resource.TypeName}/{resource.Id}' for {mode.GetLiteral()} was successful";
                if (outcome.Warnings > 0)
                    summaryMessage += $" (with {outcome.Warnings} warnings)";
                outcome.Issue.Insert(0, new OperationOutcome.IssueComponent
                {
                    Code = OperationOutcome.IssueType.Informational,
                    Severity = OperationOutcome.IssueSeverity.Information,
                    Details = new CodeableConcept(null, null, summaryMessage)
                });
            }
            return outcome;
        }

        virtual public Task<Bundle> Search(string xResourceName, IEnumerable<KeyValuePair<string, string>> parameters, int? Count, SummaryType summary, string sortby)
        {
            // This is a Brute force search implementation - just scan all the files
            Bundle resource = new Bundle();
            if (resource.Meta == null)
                resource.Meta = new Meta();
            resource.Meta.LastUpdated = DateTime.Now;
            resource.Id = new Uri("urn:uuid:" + Guid.NewGuid().ToFhirId()).OriginalString;
            resource.Type = Bundle.BundleType.Searchset;
            resource.ResourceBase = RequestDetails.BaseUri;

            Dictionary<string, Resource> entries = new Dictionary<string, Resource>();
            string filter = $"{xResourceName}.*..xml";
            IEnumerable<string> filenames = null;
            var idparam = parameters.Where(kp => kp.Key == "_id");
            List<string> usedParameters = new List<string>();
            bool elementsFilterActive = false;
            if (idparam.Any())
            {
                // Even though this is a trashy demo app, don't permit walking the file system
                filter = $"{xResourceName}.{idparam.First().Value.Replace("/", "")}.*.xml";
                filenames = Directory.EnumerateFiles(ResourceDirectory, filter);
                usedParameters.Add("_id");
            }
            foreach (var p in parameters)
            {
                if (p.Key == "_elements")
                {
                    usedParameters.Add(p.Key);
                    resource.SetAnnotation(new FilterOutputToElements(p.Value));
                    elementsFilterActive = true;
                    continue;
                }
                var r = Indexer.Search(xResourceName, p.Key, p.Value);
                if (r != null)
                {
                    if (filenames == null)
                        filenames = r;
                    else
                        filenames = filenames.Intersect(r);
                    usedParameters.Add(p.Key);
                }
            }
            if (filenames == null)
                filenames = Directory.EnumerateFiles(ResourceDirectory, filter);
            foreach (var filename in filenames)
            {
                if (!filename.EndsWith("..xml")) // skip over the version history items
                    continue;
                if (File.Exists(filename))
                {
                    using (FileStream stream = new FileStream(filename, FileMode.Open, FileAccess.Read, FileShare.Read))
                    {
                        var xr = Hl7.Fhir.Utility.SerializationUtil.XmlReaderFromStream(stream);
                        var resourceEntry = _parser.Parse<Resource>(xr);
                        if (entries.ContainsKey(resourceEntry.Id))
                        {
                            if (String.Compare(entries[resourceEntry.Id].Meta.VersionId, resourceEntry.Meta.VersionId) < 0)
                                entries[resource.Id] = resourceEntry;
                        }
                        else
                            entries.Add(resourceEntry.Id, resourceEntry);
                    }
                }
                else
                {
                    System.Diagnostics.Trace.WriteLine($"Search data out of date for file likely deleted file: {filename}");
                }
            }
            foreach (var item in entries.Values)
            {
                resource.AddResourceEntry(item,
                    ResourceIdentity.Build(RequestDetails.BaseUri,
                        item.TypeName,
                        item.Id,
                        item.Meta?.VersionId).OriginalString).Search = new Bundle.SearchComponent()
                        {
                            Mode = Bundle.SearchEntryMode.Match
                        };
                if (elementsFilterActive)
                {
                    // Add in the Meta Tag that indicates that this resource is only a partial
                    if (item.Meta == null) item.Meta = new Meta();
                    if (!item.Meta.Tag.Any(c => c.System == ResourceExtensions.SubsettedSystem && c.Code == "SUBSETTED"))
                        item.Meta.Tag.Add(new Coding(ResourceExtensions.SubsettedSystem, "SUBSETTED"));
                }
            }

            resource.Total = resource.Entry.Count(e => e.Search.Mode == Bundle.SearchEntryMode.Match);
            if (Count.HasValue)
                resource.Entry = resource.Entry.Take(Count.Value).ToList();
            if (parameters.Count(p => p.Key != "_id" && !usedParameters.Contains(p.Key)) > 0)
            {
                var outcome = new OperationOutcome();
                outcome.Issue.Add(new OperationOutcome.IssueComponent()
                {
                    Severity = OperationOutcome.IssueSeverity.Warning,
                    Code = OperationOutcome.IssueType.NotSupported,
                    Details = new CodeableConcept() { Text = $"Unsupported search parameters used: {String.Join("&", parameters.Where(p => p.Key != "_id" && !usedParameters.Contains(p.Key)).Select(k => k.Key + "=" + k.Value))}" }
                });
                resource.AddResourceEntry(outcome,
                    new Uri("urn:uuid:" + Guid.NewGuid().ToFhirId()).OriginalString).Search = new Bundle.SearchComponent()
                    {
                        Mode = Bundle.SearchEntryMode.Outcome
                    };
            }

            // Add in the navigation links
            resource.SelfLink = RequestDetails.RequestUri;

            return System.Threading.Tasks.Task.FromResult(resource);
        }

        virtual public Task<Bundle> Search(IEnumerable<KeyValuePair<string, string>> parameters, int? Count, SummaryType summary, string sortby)
        {
            // This is a Brute force search implementation - just scan all the files
            Bundle resource = new Bundle();
            if (resource.Meta == null)
                resource.Meta = new Meta();
            resource.Meta.LastUpdated = DateTime.Now;
            resource.Id = new Uri("urn:uuid:" + Guid.NewGuid().ToFhirId()).OriginalString;
            resource.Type = Bundle.BundleType.Searchset;
            resource.ResourceBase = RequestDetails.BaseUri;

            Dictionary<string, Resource> entries = new Dictionary<string, Resource>();
            string filter = $"{ResourceName}.*..xml";
            IEnumerable<string> filenames = null;
            var idparam = parameters.Where(kp => kp.Key == "_id");
            List<string> usedParameters = new List<string>();
            bool elementsFilterActive = false;
            if (idparam.Any())
            {
                // Even though this is a trashy demo app, don't permit walking the file system
                filter = $"{ResourceName}.{idparam.First().Value.Replace("/", "")}.*.xml";
                filenames = Directory.EnumerateFiles(ResourceDirectory, filter);
                usedParameters.Add("_id");
            }
            foreach (var p in parameters)
            {
                if (p.Key == "_elements")
                {
                    usedParameters.Add(p.Key);
                    resource.SetAnnotation(new FilterOutputToElements(p.Value));
                    elementsFilterActive = true;
                    continue;
                }
                var r = Indexer.Search(ResourceName, p.Key, p.Value);
                if (r != null)
                {
                    if (filenames == null)
                        filenames = r;
                    else
                        filenames = filenames.Intersect(r);
                    usedParameters.Add(p.Key);
                }
            }
            if (filenames == null)
                filenames = Directory.EnumerateFiles(ResourceDirectory, filter);
            foreach (var filename in filenames)
            {
                if (!filename.EndsWith("..xml")) // skip over the version history items
                    continue;
                if (File.Exists(filename))
                {
                    using (FileStream stream = new FileStream(filename, FileMode.Open, FileAccess.Read, FileShare.Read))
                    {
                        var xr = Hl7.Fhir.Utility.SerializationUtil.XmlReaderFromStream(stream);
                        var resourceEntry = _parser.Parse<Resource>(xr);
                        if (entries.ContainsKey(resourceEntry.Id))
                        {
                            if (String.Compare(entries[resourceEntry.Id].Meta.VersionId, resourceEntry.Meta.VersionId) < 0)
                                entries[resource.Id] = resourceEntry;
                        }
                        else
                            entries.Add(resourceEntry.Id, resourceEntry);
                    }
                }
                else
                {
                    System.Diagnostics.Trace.WriteLine($"Search data out of date for file likely deleted file: {filename}");
                }
            }
            foreach (var item in entries.Values)
            {
                resource.AddResourceEntry(item,
                    ResourceIdentity.Build(RequestDetails.BaseUri,
                        item.TypeName,
                        item.Id,
                        item.Meta?.VersionId).OriginalString).Search = new Bundle.SearchComponent()
                        {
                            Mode = Bundle.SearchEntryMode.Match
                        };
                if (elementsFilterActive)
                {
                    // Add in the Meta Tag that indicates that this resource is only a partial
                    if (item.Meta == null) item.Meta = new Meta();
                    if (!item.Meta.Tag.Any(c => c.System == ResourceExtensions.SubsettedSystem && c.Code == "SUBSETTED"))
                        item.Meta.Tag.Add(new Coding(ResourceExtensions.SubsettedSystem, "SUBSETTED"));
                }
            }

            resource.Total = resource.Entry.Count(e => e.Search.Mode == Bundle.SearchEntryMode.Match);
            if (Count.HasValue)
                resource.Entry = resource.Entry.Take(Count.Value).ToList();
            if (parameters.Count(p => p.Key != "_id" && !usedParameters.Contains(p.Key)) > 0)
            {
                var outcome = new OperationOutcome();
                outcome.Issue.Add(new OperationOutcome.IssueComponent()
                {
                    Severity = OperationOutcome.IssueSeverity.Warning,
                    Code = OperationOutcome.IssueType.NotSupported,
                    Details = new CodeableConcept() { Text = $"Unsupported search parameters used: {String.Join("&", parameters.Where(p => p.Key != "_id" && !usedParameters.Contains(p.Key)).Select(k => k.Key + "=" + k.Value))}" }
                });
                resource.AddResourceEntry(outcome,
                    new Uri("urn:uuid:" + Guid.NewGuid().ToFhirId()).OriginalString).Search = new Bundle.SearchComponent()
                    {
                        Mode = Bundle.SearchEntryMode.Outcome
                    };
            }

            // Add in the navigation links
            resource.SelfLink = RequestDetails.RequestUri;

            return System.Threading.Tasks.Task.FromResult(resource);
        }

        virtual public Task<Bundle> TypeHistory(DateTimeOffset? since, DateTimeOffset? Till, int? Count, SummaryType summary)
        {
            Bundle result = new Bundle();
            result.Meta = new Meta()
            {
                LastUpdated = DateTime.Now
            };
            result.Id = new Uri("urn:uuid:" + Guid.NewGuid().ToString("n")).OriginalString;
            result.Type = Bundle.BundleType.History;

            var files = Directory.EnumerateFiles(ResourceDirectory, $"{ResourceName}.*.*.xml");
            foreach (var filename in files)
            {
                if (filename.EndsWith("..xml")) // this is the current version file, the version number file will have the real data
                    continue;
                var resource = _parser.Parse<Resource>(File.ReadAllText(filename));
                result.AddResourceEntry(resource,
                    ResourceIdentity.Build(RequestDetails.BaseUri,
                        resource.TypeName,
                        resource.Id,
                        resource.Meta.VersionId).OriginalString);
            }
            result.Total = result.Entry.Count;
            result.Entry.Sort((x, y) => { return y.Resource.Meta.LastUpdated.Value.CompareTo(x.Resource.Meta.LastUpdated.Value); });

            // also need to set the page links

            return System.Threading.Tasks.Task.FromResult(result);
        }
    }
}
