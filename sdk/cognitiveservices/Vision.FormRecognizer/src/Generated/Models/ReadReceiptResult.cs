// <auto-generated>
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License. See License.txt in the project root for
// license information.
//
// Code generated by Microsoft (R) AutoRest Code Generator.
// Changes may cause incorrect behavior and will be lost if the code is
// regenerated.
// </auto-generated>

namespace Microsoft.Azure.CognitiveServices.FormRecognizer.Models
{
    using Newtonsoft.Json;
    using System.Collections;
    using System.Collections.Generic;
    using System.Linq;

    /// <summary>
    /// Analysis result of the 'Batch Read Receipt' operation.
    /// </summary>
    public partial class ReadReceiptResult
    {
        /// <summary>
        /// Initializes a new instance of the ReadReceiptResult class.
        /// </summary>
        public ReadReceiptResult()
        {
            CustomInit();
        }

        /// <summary>
        /// Initializes a new instance of the ReadReceiptResult class.
        /// </summary>
        /// <param name="status">Status of the read operation. Possible values
        /// include: 'Not Started', 'Running', 'Failed', 'Succeeded'</param>
        /// <param name="recognitionResults">Text recognition result of the
        /// 'Batch Read Receipt' operation.</param>
        /// <param name="understandingResults">Semantic understanding result of
        /// the 'Batch Read Receipt' operation.</param>
        public ReadReceiptResult(TextOperationStatusCodes status = default(TextOperationStatusCodes), IList<TextRecognitionResult> recognitionResults = default(IList<TextRecognitionResult>), IList<UnderstandingResult> understandingResults = default(IList<UnderstandingResult>))
        {
            Status = status;
            RecognitionResults = recognitionResults;
            UnderstandingResults = understandingResults;
            CustomInit();
        }

        /// <summary>
        /// An initialization method that performs custom operations like setting defaults
        /// </summary>
        partial void CustomInit();

        /// <summary>
        /// Gets or sets status of the read operation. Possible values include:
        /// 'Not Started', 'Running', 'Failed', 'Succeeded'
        /// </summary>
        [JsonProperty(PropertyName = "status")]
        public TextOperationStatusCodes Status { get; set; }

        /// <summary>
        /// Gets or sets text recognition result of the 'Batch Read Receipt'
        /// operation.
        /// </summary>
        [JsonProperty(PropertyName = "recognitionResults")]
        public IList<TextRecognitionResult> RecognitionResults { get; set; }

        /// <summary>
        /// Gets or sets semantic understanding result of the 'Batch Read
        /// Receipt' operation.
        /// </summary>
        [JsonProperty(PropertyName = "understandingResults")]
        public IList<UnderstandingResult> UnderstandingResults { get; set; }

    }
}
