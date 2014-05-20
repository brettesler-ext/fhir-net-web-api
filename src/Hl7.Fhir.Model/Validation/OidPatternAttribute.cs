﻿/* 
 * Copyright (c) 2014, Furore (info@furore.com) and contributors
 * See the file CONTRIBUTORS for details.
 * 
 * This file is licensed under the BSD 3-Clause license
 * available at https://raw.githubusercontent.com/ewoutkramer/fhir-net-api/master/LICENSE
 */

using Hl7.Fhir.Model;
using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Hl7.Fhir.Validation
{
    [AttributeUsage(AttributeTargets.Property, Inherited = false, AllowMultiple = false)]
    public class OidPatternAttribute : ValidationAttribute
    {
        protected override ValidationResult IsValid(object value, ValidationContext validationContext)
        {
            if (value == null) return ValidationResult.Success;

            if (value.GetType() != typeof(string))
                throw new ArgumentException("OidPatternAttribute can only be applied to string properties");

            if (OidPatternAttribute.IsValid((string)value))
                return ValidationResult.Success;
            else
                return FhirValidator.BuildResult(validationContext, "{0} is not a correctly formatted Oid", (string)value);
        }

        public static bool IsValid(string value)
        {
            return Regex.IsMatch(value, "^" + Oid.PATTERN + "$", RegexOptions.Singleline);
        }
    }
}
