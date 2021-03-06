﻿// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Microsoft.CodeAnalysis.Sarif.PatternMatcher
{
    public class ValidatingVisitor : SarifRewritingVisitor
    {
        private readonly ValidatorsCache _validators;
        private Run _run;

        public ValidatingVisitor(ValidatorsCache validators)
        {
            _validators = validators;
        }

        public override Run VisitRun(Run node)
        {
            _run = node;
            return base.VisitRun(node);
        }

        public override Result VisitResult(Result node)
        {
            if (node.Fingerprints == null ||
                !node.Fingerprints.TryGetValue(SearchSkimmer.ValidationFingerprint, out string fingerprint))
            {
                return node;
            }

            ReportingDescriptor rule = node.GetRule(_run);

            ValidationMethodPair validationPair =
                ValidatorsCache.GetValidationMethodPair(rule.Name, _validators.RuleNameToValidationMethods);

            if (validationPair.IsValidDynamic != null)
            {
                // Our validation messages currently look like so.
                // {0:scanTarget}' contains {1:validationPrefix}{2:encoding}{3:secretKind}{4:validationSuffix}{5:validatorMessage}
                string message = null;

                FailureLevel level = node.Level;
                Validation state =
                    ValidatorsCache.ValidateDynamicHelper(validationPair.IsValidDynamic,
                                                          ref fingerprint,
                                                          ref message);

                string validationPrefix = node.Message.Arguments[1];
                string validationSuffix = node.Message.Arguments[4];

                SearchSkimmer.SetPropertiesBasedOnValidationState(state,
                                                                  context: null,
                                                                  ref level,
                                                                  ref validationPrefix,
                                                                  ref validationSuffix,
                                                                  ref message,
                                                                  pluginSupportsDynamicValidation: true);

                node.Level = level;
                node.Message.Arguments[1] = validationPrefix;
                node.Message.Arguments[4] = validationSuffix;
                node.Message.Arguments[5] = SearchSkimmer.NormalizeValidatorMessage(message);
            }

            return node;
        }
    }
}
