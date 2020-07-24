﻿// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EnsureThat;
using Hl7.Fhir.Model;
using Microsoft.Health.Extensions.DependencyInjection;
using Microsoft.Health.Fhir.Core.Exceptions;
using Microsoft.Health.Fhir.Core.Extensions;
using Microsoft.Health.Fhir.Core.Features.Persistence;
using Microsoft.Health.Fhir.Core.Features.Search;
using Microsoft.Health.Fhir.Core.Models;

namespace Microsoft.Health.Fhir.Core.Features.Operations.Export
{
    /// <summary>
    /// Class to get member ids and types out of a group. Split between common and version specifc code due to the change between Stu3 and R4 to the ResourceReference object.
    /// </summary>
    public class GroupMemberExtractor : IGroupMemberExtractor
    {
        private readonly IScoped<IFhirDataStore> _fhirDataStore;
        private readonly ResourceDeserializer _resourceDeserializer;
        private readonly IReferenceToElementResolver _referenceToElementResolver;

        public GroupMemberExtractor(
            IScoped<IFhirDataStore> fhirDataStore,
            ResourceDeserializer resourceDeserializer,
            IReferenceToElementResolver referenceToElementResolver)
        {
            EnsureArg.IsNotNull(fhirDataStore, nameof(fhirDataStore));
            EnsureArg.IsNotNull(resourceDeserializer, nameof(resourceDeserializer));
            EnsureArg.IsNotNull(referenceToElementResolver, nameof(referenceToElementResolver));

            _fhirDataStore = fhirDataStore;
            _resourceDeserializer = resourceDeserializer;
            _referenceToElementResolver = referenceToElementResolver;
        }

        public async Task<HashSet<string>> GetGroupPatientIds(string groupId, DateTimeOffset groupMembershipTime, CancellationToken cancellationToken, HashSet<string> groupsAlreadyChecked = null)
        {
            if (groupsAlreadyChecked == null)
            {
                groupsAlreadyChecked = new HashSet<string>();
            }

            groupsAlreadyChecked.Add(groupId);

            var groupContents = await GetGroupMembers(groupId, groupMembershipTime, cancellationToken);

            var patientIds = new HashSet<string>();

            foreach (Tuple<string, string> entity in groupContents)
            {
                var resourceId = entity.Item1;
                var resourceType = entity.Item2;

                // Only Patient resources and their compartment resources are exported as part of a Group export. All other resource types are ignored.
                // Nested Group resources are checked to see if they contain other Patients.
                switch (resourceType)
                {
                    case KnownResourceTypes.Patient:
                        patientIds.Add(resourceId);
                        break;
                    case KnownResourceTypes.Group:
                        // need to check that loops aren't happening
                        if (!groupsAlreadyChecked.Contains(resourceId))
                        {
                            patientIds.UnionWith(await GetGroupPatientIds(resourceId, groupMembershipTime, cancellationToken, groupsAlreadyChecked));
                        }

                        break;
                }
            }

            return patientIds;
        }

        public async Task<List<Tuple<string, string>>> GetGroupMembers(string groupId, DateTimeOffset groupMembershipTime, CancellationToken cancellationToken, bool includeInactiveMembers = false)
        {
            var groupResource = await _fhirDataStore.Value.GetAsync(new ResourceKey(KnownResourceTypes.Group, groupId), cancellationToken);

            if (groupResource == null)
            {
                throw new ResourceNotFoundException($"Group {groupId} was not found.");
            }

            var group = _resourceDeserializer.Deserialize(groupResource);
            var groupContents = group.ToPoco<Group>().Member;

            var members = new List<Tuple<string, string>>();

            foreach (Group.MemberComponent member in groupContents)
            {
                var fhirGroupMembershipTime = new FhirDateTime(groupMembershipTime);
                if (
                    (member.Inactive == null
                    || member.Inactive == false
                    || member.Period?.EndElement == null
                    || member.Period?.EndElement > fhirGroupMembershipTime
                    || includeInactiveMembers)
                    && (member.Period?.StartElement == null
                    || member.Period?.StartElement < fhirGroupMembershipTime))
                {
                    var element = _referenceToElementResolver.Resolve(member.Entity.Reference);
                    string id = (string)element.Children("id").First().Value;
                    string resourceType = element.InstanceType;

                    members.Add(Tuple.Create(id, resourceType));
                }
            }

            return members;
        }
    }
}
