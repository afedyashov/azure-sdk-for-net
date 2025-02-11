﻿// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License. See License.txt in the project root for
// license information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Azure.Core.Testing;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Blobs.Specialized;
using Azure.Storage.Common;
using Azure.Storage.Test;
using Azure.Storage.Test.Shared;
using NUnit.Framework;
using TestConstants = Azure.Storage.Test.Constants;

namespace Azure.Storage.Blobs.Test
{
    [TestFixture]
    public class BlobClientTests : BlobTestBase
    {
        public BlobClientTests()
            : base(/* Use RecordedTestMode.Record here to re-record just these tests */)
        {
        }

        [Test]
        public void Ctor_ConnectionString()
        {
            var accountName = "accountName";
            var accountKey = Convert.ToBase64String(new byte[] { 0, 1, 2, 3, 4, 5 });

            var credentials = new SharedKeyCredentials(accountName, accountKey);
            var blobEndpoint = new Uri("http://127.0.0.1/" + accountName);
            var blobSecondaryEndpoint = new Uri("http://127.0.0.1/" + accountName + "-secondary");

            var connectionString = new StorageConnectionString(credentials, (blobEndpoint, blobSecondaryEndpoint), (default, default), (default, default), (default, default));

            var containerName = this.GetNewContainerName();
            var blobName = this.GetNewBlobName();

            var blob = this.InstrumentClient(new BlobClient(connectionString.ToString(true), containerName, blobName, this.GetOptions()));

            var builder = new BlobUriBuilder(blob.Uri);

            Assert.AreEqual(containerName, builder.ContainerName);
            Assert.AreEqual(blobName, builder.BlobName);
            Assert.AreEqual("accountName", builder.AccountName);
        }

        [Test]
        public async Task DownloadAsync()
        {
            using (this.GetNewContainer(out var container))
            {
                // Arrange
                var data = this.GetRandomBuffer(Constants.KB);
                var blob = this.InstrumentClient(container.GetBlockBlobClient(this.GetNewBlobName()));
                using (var stream = new MemoryStream(data))
                {
                    await blob.UploadAsync(stream);
                }

                // Act
                var response = await blob.DownloadAsync();
                
                // Assert
                Assert.AreEqual(data.Length, response.Value.ContentLength);
                var actual = new MemoryStream();
                await response.Value.Content.CopyToAsync(actual);
                TestHelper.AssertSequenceEqual(data, actual.ToArray());
            }
        }

        [Test]
        public async Task DownloadAsync_WithUnreliableConnection()
        {
            // Arrange
            var service = this.InstrumentClient(
                new BlobServiceClient(
                    new Uri(TestConfigurations.DefaultTargetTenant.BlobServiceEndpoint),
                    this.GetFaultyBlobConnectionOptions(
                        new SharedKeyCredentials(TestConfigurations.DefaultTargetTenant.AccountName, TestConfigurations.DefaultTargetTenant.AccountKey),
                        raiseAt: 256 * Constants.KB,
                        raise: new Exception("Unexpected"))));

            using (this.GetNewContainer(out var container, service: service))
            {
                var data = this.GetRandomBuffer(Constants.KB);

                var blob = this.InstrumentClient(container.GetBlockBlobClient(this.GetNewBlobName()));
                using (var stream = new MemoryStream(data))
                {
                    await blob.UploadAsync(stream);
                }

                // Act
                var response = await blob.DownloadAsync();

                // Assert
                Assert.AreEqual(data.Length, response.Value.ContentLength);
                var actual = new MemoryStream();
                await response.Value.Content.CopyToAsync(actual);
                TestHelper.AssertSequenceEqual(data, actual.ToArray());
            }
        }

        [Test]
        public async Task DownloadAsync_Range()
        {
            using (this.GetNewContainer(out var container))
            {
                // Arrange
                var data = this.GetRandomBuffer(10 * Constants.KB);
                var blob = this.InstrumentClient(container.GetBlockBlobClient(this.GetNewBlobName()));
                var offset = Constants.KB;
                var count = 2 * Constants.KB;
                using (var stream = new MemoryStream(data))
                {
                    await blob.UploadAsync(stream);
                }

                // Act
                var response = await blob.DownloadAsync(range: new HttpRange(offset, count));

                // Assert
                Assert.AreEqual(count, response.Value.ContentLength);
                var actual = new MemoryStream();
                await response.Value.Content.CopyToAsync(actual);
                Assert.AreEqual(count, actual.Length);
                TestHelper.AssertSequenceEqual(data.Skip(offset).Take(count), actual.ToArray());
            }
        }

        [Test]
        public async Task DownloadAsync_AccessConditions()
        {
            var garbageLeaseId = this.GetGarbageLeaseId();
            foreach (var parameters in this.NoLease_AccessConditions_Data)
            {
                using (this.GetNewContainer(out var container))
                {
                    // Arrange
                    var data = this.GetRandomBuffer(Constants.KB);
                    var blob = this.InstrumentClient(container.GetBlockBlobClient(this.GetNewBlobName()));
                    using (var stream = new MemoryStream(data))
                    {
                        await blob.UploadAsync(stream);
                    }

                    parameters.Match = await this.SetupBlobMatchCondition(blob, parameters.Match);
                    parameters.LeaseId = await this.SetupBlobLeaseCondition(blob, parameters.LeaseId, garbageLeaseId);
                    var accessConditions = this.BuildAccessConditions(parameters);

                    // Act
                    var response = await blob.DownloadAsync(accessConditions: accessConditions);

                    // Assert
                    Assert.AreEqual(data.Length, response.Value.ContentLength);
                    var actual = new MemoryStream();
                    await response.Value.Content.CopyToAsync(actual);
                    TestHelper.AssertSequenceEqual(data, actual.ToArray());
                }
            }
        }

        [Test]
        public async Task DownloadAsync_AccessConditionsFail()
        {
            var garbageLeaseId = this.GetGarbageLeaseId();
            foreach (var parameters in this.GetAccessConditionsFail_Data(garbageLeaseId))
            {
                using (this.GetNewContainer(out var container))
                {
                    // Arrange
                    var data = this.GetRandomBuffer(Constants.KB);
                    var blob = this.InstrumentClient(container.GetBlockBlobClient(this.GetNewBlobName()));
                    using (var stream = new MemoryStream(data))
                    {
                        await blob.UploadAsync(stream);
                    }

                    parameters.NoneMatch = await this.SetupBlobMatchCondition(blob, parameters.NoneMatch);
                    var accessConditions = this.BuildAccessConditions(parameters);

                    // Act
                    await TestHelper.AssertExpectedExceptionAsync<StorageRequestFailedException>(
                        blob.DownloadAsync(accessConditions: accessConditions),
                        e => { });
                }
            }
        }

        [Test]
        public async Task DownloadAsync_MD5()
        {
            using (this.GetNewContainer(out var container))
            {
                // Arrange
                var data = this.GetRandomBuffer(10 * Constants.KB);
                var blob = this.InstrumentClient(container.GetBlockBlobClient(this.GetNewBlobName()));
                var offset = Constants.KB;
                var count = 2 * Constants.KB;
                using (var stream = new MemoryStream(data))
                {
                    await blob.UploadAsync(stream);
                }

                // Act
                var response = await blob.DownloadAsync(
                    range: new HttpRange(offset, count), 
                    rangeGetContentHash: true);

                // Assert
                var expectedMD5 = MD5.Create().ComputeHash(data.Skip(offset).Take(count).ToArray());
                TestHelper.AssertSequenceEqual(expectedMD5, response.Value.ContentHash);
            }
        }

        [Test]
        public async Task DownloadAsync_Error()
        {
            using (this.GetNewContainer(out var container))
            {
                // Arrange
                var blob = this.InstrumentClient(container.GetBlockBlobClient(this.GetNewBlobName()));

                // Act
                await TestHelper.AssertExpectedExceptionAsync<StorageRequestFailedException>(
                    blob.DownloadAsync(),
                    e => Assert.AreEqual("The specified blob does not exist.", e.Message.Split('\n')[0]));
            }
        }

        [Test]
        public async Task StartCopyFromUriAsync()
        {
            using (this.GetNewContainer(out var container))
            {
                // Arrange
                var srcBlob = await this.GetNewBlobClient(container);
                var destBlob = this.InstrumentClient(container.GetBlockBlobClient(this.GetNewBlobName()));

                // Act
                var response = await destBlob.StartCopyFromUriAsync(srcBlob.Uri);

                // Assert
                // data copied within an account, so copy should be instantaneous
                Assert.AreEqual(CopyStatus.Success, response.Value.CopyStatus);
            }
        }

        [Test]
        public async Task StartCopyFromUriAsync_Metadata()
        {
            using (this.GetNewContainer(out var container))
            {
                // Arrange
                var srcBlob = await this.GetNewBlobClient(container);
                var metadata = this.BuildMetadata();

                var destBlob = this.InstrumentClient(container.GetBlockBlobClient(this.GetNewBlobName()));

                // Act
                await destBlob.StartCopyFromUriAsync(
                    source: srcBlob.Uri,
                    metadata: metadata);

                // Assert
                var response = await destBlob.GetPropertiesAsync();
                this.AssertMetadataEquality(metadata, response.Value.Metadata);
            }
        }

        [Test]
        public async Task StartCopyFromUriAsync_Source_AccessConditions()
        {
            var garbageLeaseId = this.GetGarbageLeaseId();
            foreach (var parameters in this.NoLease_AccessConditions_Data)
            {
                using (this.GetNewContainer(out var container))
                {
                    // Arrange
                    var srcBlob = await this.GetNewBlobClient(container);

                    parameters.Match = await this.SetupBlobMatchCondition(srcBlob, parameters.Match);
                    parameters.LeaseId = await this.SetupBlobLeaseCondition(srcBlob, parameters.LeaseId, garbageLeaseId);
                    var sourceAccessConditions = this.BuildAccessConditions(
                        parameters: parameters);

                    var destBlob = this.InstrumentClient(container.GetBlockBlobClient(this.GetNewBlobName()));

                    // Act
                    var response = await destBlob.StartCopyFromUriAsync(
                        source: srcBlob.Uri,
                        sourceAccessConditions: sourceAccessConditions);

                    // Assert
                    Assert.IsNotNull(response.GetRawResponse().Headers.RequestId);
                }
            }
        }

        [Test]
        public async Task StartCopyFromUriAsync_Source_AccessConditionsFail()
        {
            foreach (var parameters in this.NoLease_AccessConditionsFail_Data)
            {
                using (this.GetNewContainer(out var container))
                {
                    // Arrange
                    var srcBlob = await this.GetNewBlobClient(container);

                    parameters.NoneMatch = await this.SetupBlobMatchCondition(srcBlob, parameters.NoneMatch);

                    var sourceAccessConditions = this.BuildAccessConditions(
                        parameters: parameters,
                        lease: false);

                    var destBlob = this.InstrumentClient(container.GetBlockBlobClient(this.GetNewBlobName()));

                    // Act
                    await TestHelper.AssertExpectedExceptionAsync<StorageRequestFailedException>(
                        destBlob.StartCopyFromUriAsync(
                            source: srcBlob.Uri,
                            sourceAccessConditions: sourceAccessConditions),
                        e => { });
                }
            }
        }

        [Test]
        public async Task StartCopyFromUriAsync_Destination_AccessConditions()
        {
            var garbageLeaseId = this.GetGarbageLeaseId();
            foreach (var parameters in this.AccessConditions_Data)
            {
                using (this.GetNewContainer(out var container))
                {
                    // Arrange
                    var data = this.GetRandomBuffer(Constants.KB);
                    var srcBlob = this.InstrumentClient(container.GetBlockBlobClient(this.GetNewBlobName()));
                    using (var stream = new MemoryStream(data))
                    {
                        await srcBlob.UploadAsync(stream);
                    }
                    var destBlob = this.InstrumentClient(container.GetBlockBlobClient(this.GetNewBlobName()));

                    // destBlob needs to exist so we can get its lease and etag
                    using (var stream = new MemoryStream(data))
                    {
                        await destBlob.UploadAsync(stream);
                    }

                    parameters.Match = await this.SetupBlobMatchCondition(destBlob, parameters.Match);
                    parameters.LeaseId = await this.SetupBlobLeaseCondition(destBlob, parameters.LeaseId, garbageLeaseId);

                    var accessConditions = this.BuildAccessConditions(parameters: parameters);

                    // Act
                    var response = await destBlob.StartCopyFromUriAsync(
                        source: srcBlob.Uri,
                        destinationAccessConditions: accessConditions);

                    // Assert
                    Assert.IsNotNull(response.GetRawResponse().Headers.RequestId);
                }
            }
        }

        [Test]
        public async Task StartCopyFromUriAsync_Destination_AccessConditionsFail()
        {
            var garbageLeaseId = this.GetGarbageLeaseId();
            foreach (var parameters in this.GetAccessConditionsFail_Data(garbageLeaseId))
            {
                using (this.GetNewContainer(out var container))
                {
                    // Arrange
                    var data = this.GetRandomBuffer(Constants.KB);
                    var srcBlob = this.InstrumentClient(container.GetBlockBlobClient(this.GetNewBlobName()));
                    using (var stream = new MemoryStream(data))
                    {
                        await srcBlob.UploadAsync(stream);
                    }

                    // destBlob needs to exist so we can get its etag
                    var destBlob = this.InstrumentClient(container.GetBlockBlobClient(this.GetNewBlobName()));
                    using (var stream = new MemoryStream(data))
                    {
                        await destBlob.UploadAsync(stream);
                    }

                    parameters.NoneMatch = await this.SetupBlobMatchCondition(destBlob, parameters.NoneMatch);
                    var accessConditions = this.BuildAccessConditions(parameters: parameters);

                    // Act
                    await TestHelper.AssertExpectedExceptionAsync<StorageRequestFailedException>(
                        destBlob.StartCopyFromUriAsync(
                            source: srcBlob.Uri,
                            destinationAccessConditions: accessConditions),
                        e => { });
                }
            }
        }

        [Test]
        public async Task StartCopyFromUriAsync_Error()
        {
            using (this.GetNewContainer(out var container))
            {
                // Arrange
                var srcBlob = this.InstrumentClient(container.GetBlockBlobClient(this.GetNewBlobName()));
                var destBlob = this.InstrumentClient(container.GetBlockBlobClient(this.GetNewBlobName()));

                // Act
                await TestHelper.AssertExpectedExceptionAsync<StorageRequestFailedException>(
                    destBlob.StartCopyFromUriAsync(srcBlob.Uri),
                    e => Assert.AreEqual("BlobNotFound", e.ErrorCode));
            }
        }

        [Test]
        public async Task AbortCopyFromUriAsync()
        {
            using (this.GetNewContainer(out var container))
            {
                // Arrange
                await container.SetAccessPolicyAsync(PublicAccessType.Blob);
                var data = this.GetRandomBuffer(8 * Constants.MB);

                var srcBlob = this.InstrumentClient(container.GetBlockBlobClient(this.GetNewBlobName()));
                using (var stream = new MemoryStream(data))
                {
                    await srcBlob.UploadAsync(stream);
                }

                var secondaryService = this.GetServiceClient_SecondaryAccount_SharedKey();
                using (this.GetNewContainer(out var destContainer, service: secondaryService))
                {
                    var destBlob = this.InstrumentClient(destContainer.GetBlockBlobClient(this.GetNewBlobName()));

                    var copyResponse = await destBlob.StartCopyFromUriAsync(srcBlob.Uri);

                    // Act
                    try
                    {
                        var response = await destBlob.AbortCopyFromUriAsync(copyResponse.Value.CopyId);

                        // Assert
                        Assert.IsNotNull(response.Headers.RequestId);
                    }
                    catch (StorageRequestFailedException e) when (e.ErrorCode == "NoPendingCopyOperation")
                    {
                        this.WarnCopyCompletedTooQuickly();
                    }
                }
            }
        }

        [Test]
        public async Task AbortCopyFromUriAsync_Lease()
        {
            using (this.GetNewContainer(out var container))
            {
                // Arrange
                await container.SetAccessPolicyAsync(PublicAccessType.Blob);
                var data = this.GetRandomBuffer(8 * Constants.MB);

                var srcBlob = this.InstrumentClient(container.GetBlockBlobClient(this.GetNewBlobName()));
                using (var stream = new MemoryStream(data))
                {
                    await srcBlob.UploadAsync(stream);
                }
                var secondaryService = this.GetServiceClient_SecondaryAccount_SharedKey();
                using (this.GetNewContainer(out var destContainer, service: secondaryService))
                {
                    var destBlob = this.InstrumentClient(destContainer.GetBlockBlobClient(this.GetNewBlobName()));
                    using (var stream = new MemoryStream(data))
                    {
                        await destBlob.UploadAsync(stream);
                    }

                    var duration = -1;
                    var leaseResponse = await destBlob.AcquireLeaseAsync(duration);

                    var copyResponse = await destBlob.StartCopyFromUriAsync(
                        source: srcBlob.Uri,
                        destinationAccessConditions: new BlobAccessConditions
                        {
                            LeaseAccessConditions = new LeaseAccessConditions
                            {
                                LeaseId = leaseResponse.Value.LeaseId
                            }
                        });


                    // Act
                    try
                    {
                        var response = await destBlob.AbortCopyFromUriAsync(
                            copyId: copyResponse.Value.CopyId,
                            leaseAccessConditions: new LeaseAccessConditions
                            {
                                LeaseId = leaseResponse.Value.LeaseId
                            });

                        // Assert
                        Assert.IsNotNull(response.Headers.RequestId);
                    }
                    catch (StorageRequestFailedException e) when (e.ErrorCode == "NoPendingCopyOperation")
                    {
                        this.WarnCopyCompletedTooQuickly();
                    }
                }
            }
        }

        [Test]
        public async Task AbortCopyFromUriAsync_LeaseFail()
        {
            using (this.GetNewContainer(out var container))
            {
                // Arrange
                await container.SetAccessPolicyAsync(PublicAccessType.Blob);
                var data = this.GetRandomBuffer(8 * Constants.MB);

                var srcBlob = this.InstrumentClient(container.GetBlockBlobClient(this.GetNewBlobName()));
                using (var stream = new MemoryStream(data))
                {
                    await srcBlob.UploadAsync(stream);
                }
                var secondaryService = this.GetServiceClient_SecondaryAccount_SharedKey();
                using (this.GetNewContainer(out var destContainer, service: secondaryService))
                {
                    var destBlob = this.InstrumentClient(destContainer.GetBlockBlobClient(this.GetNewBlobName()));
                    using (var stream = new MemoryStream(data))
                    {
                        await destBlob.UploadAsync(stream);
                    }

                    var copyResponse = await destBlob.StartCopyFromUriAsync(
                        source: srcBlob.Uri);

                    var leaseId = this.Recording.Random.NewGuid().ToString();

                    // Act
                    try
                    {
                        await TestHelper.AssertExpectedExceptionAsync<StorageRequestFailedException>(
                            destBlob.AbortCopyFromUriAsync(
                                copyId: copyResponse.Value.CopyId,
                                leaseAccessConditions: new LeaseAccessConditions
                                {
                                    LeaseId = leaseId
                                }),
                            e =>
                            {
                                switch (e.ErrorCode)
                                {
                                    case "NoPendingCopyOperation":
                                        this.WarnCopyCompletedTooQuickly();
                                        break;
                                    default:
                                        Assert.AreEqual("LeaseNotPresentWithBlobOperation", e.ErrorCode);
                                        break;
                                }
                            }
                            );
                    }
                    catch (StorageRequestFailedException e) when (e.ErrorCode == "NoPendingCopyOperation")
                    {
                        this.WarnCopyCompletedTooQuickly();
                    }
                }
            }
        }

        [Test]
        public async Task AbortCopyFromUriAsync_Error()
        {
            using (this.GetNewContainer(out var container))
            {
                // Arrange
                var blob = this.InstrumentClient(container.GetBlockBlobClient(this.GetNewBlobName()));
                var copyId = this.Recording.Random.NewGuid().ToString();

                // Act
                await TestHelper.AssertExpectedExceptionAsync<StorageRequestFailedException>(
                    blob.AbortCopyFromUriAsync(copyId),
                    e => Assert.AreEqual("BlobNotFound", e.ErrorCode));
            }
        }

        [Test]
        public async Task DeleteAsync()
        {
            using (this.GetNewContainer(out var container))
            {
                // Arrange
                var blob = await this.GetNewBlobClient(container);

                // Act
                var response = await blob.DeleteAsync();

                // Assert
                Assert.IsNotNull(response.Headers.RequestId);
            }
        }

        [Test]
        public async Task DeleteAsync_Options()
        {
            using (this.GetNewContainer(out var container))
            {
                // Arrange
                var blob = await this.GetNewBlobClient(container);
                await blob.CreateSnapshotAsync();

                // Act
                await blob.DeleteAsync(deleteOptions: DeleteSnapshotsOption.Only);

                // Assert
                var response = await blob.GetPropertiesAsync();
                Assert.IsNotNull(response.GetRawResponse().Headers.RequestId);
            }
        }

        [Test]
        public async Task DeleteAsync_AccessConditions()
        {
            var garbageLeaseId = this.GetGarbageLeaseId();
            foreach (var parameters in this.AccessConditions_Data)
            {
                using (this.GetNewContainer(out var container))
                {
                    // Arrange
                    var blob = await this.GetNewBlobClient(container);

                    parameters.Match = await this.SetupBlobMatchCondition(blob, parameters.Match);
                    parameters.LeaseId = await this.SetupBlobLeaseCondition(blob, parameters.LeaseId, garbageLeaseId);
                    var accessConditions = this.BuildAccessConditions(
                        parameters: parameters,
                        lease: true);

                    // Act
                    var response = await blob.DeleteAsync(accessConditions: accessConditions);

                    // Assert
                    Assert.IsNotNull(response.Headers.RequestId);
                }
            }
        }

        [Test]
        public async Task DeleteAsync_AccessConditionsFail()
        {
            var garbageLeaseId = this.GetGarbageLeaseId();
            foreach (var parameters in this.GetAccessConditionsFail_Data(garbageLeaseId))
            {
                using (this.GetNewContainer(out var container))
                {
                    // Arrange
                    var blob = await this.GetNewBlobClient(container);

                    parameters.NoneMatch = await this.SetupBlobMatchCondition(blob, parameters.NoneMatch);
                    var accessConditions = this.BuildAccessConditions(
                        parameters: parameters,
                        lease: true);

                    // Act
                    await this.AssertExpectedExceptionAsync<StorageRequestFailedException, Response>(
                        blob.DeleteAsync(accessConditions: accessConditions),
                        e => { });
                }
            }
        }

        [Test]
        public async Task DeleteAsync_Error()
        {
            using (this.GetNewContainer(out var container))
            {
                // Arrange
                var blob = this.InstrumentClient(container.GetBlockBlobClient(this.GetNewBlobName()));

                // Act
                await this.AssertExpectedExceptionAsync<StorageRequestFailedException, Response>(
                    blob.DeleteAsync(),
                    e => Assert.AreEqual("BlobNotFound", e.ErrorCode));
            }
        }

        //[Test]
        //public async Task DeleteAsync_Batch()
        //{
        //    using (this.GetNewContainer(out var container, serviceUri: this.GetServiceUri_PreviewAccount_SharedKey()))
        //    {
        //        const int blobSize = Constants.KB;
        //        var data = this.GetRandomBuffer(blobSize);

        //        var blob1 = this.InstrumentClient(container.GetBlockBlobClient(this.GetNewBlobName()));
        //        using (var stream = new MemoryStream(data))
        //        {
        //            await blob1.UploadAsync(stream);
        //        }

        //        var blob2 = this.InstrumentClient(container.GetBlockBlobClient(this.GetNewBlobName()));
        //        using (var stream = new MemoryStream(data))
        //        {
        //            await blob2.UploadAsync(stream);
        //        }

        //        var batch =
        //            blob1.DeleteAsync()
        //            .And(blob2.DeleteAsync())
        //            ;

        //        var result = await batch;

        //        Assert.IsNotNull(result);
        //        Assert.AreEqual(2, result.Length);
        //        Assert.IsNotNull(result[0].RequestId);
        //        Assert.IsNotNull(result[1].RequestId);
        //    }
        //}

        [Test]
        [NonParallelizable]
        public async Task UndeleteAsync()
        {
            using (this.GetNewContainer(out var container))
            {
                // Arrange
                await this.EnableSoftDelete();
                try
                {
                    var blob = await this.GetNewBlobClient(container);
                    await blob.DeleteAsync();

                    // Act
                    var response = await blob.UndeleteAsync();

                    // Assert
                    response.Headers.TryGetValue("x-ms-version", out var version);
                    Assert.IsNotNull(version);
                }
                catch (StorageRequestFailedException ex) when (ex.ErrorCode == StorageErrorCode.BlobNotFound)
                {
                    Assert.Inconclusive("Delete may have happened before soft delete was fully enabled!");
                }
                finally
                {
                    // Cleanup
                    await this.DisableSoftDelete();
                }
            }
        }

        [Test]
        public async Task UndeleteAsync_Error()
        {
            using (this.GetNewContainer(out var container))
            {
                // Arrange
                var blob = this.InstrumentClient(container.GetBlockBlobClient(this.GetNewBlobName()));

                // Act
                await TestHelper.AssertExpectedExceptionAsync<StorageRequestFailedException>(
                    blob.UndeleteAsync(),
                    e => Assert.AreEqual("BlobNotFound", e.ErrorCode));
            }
        }

        [Test]
        public async Task GetAccountInfoAsync()
        {
            using (this.GetNewContainer(out var container))
            {
                // Arrange
                var blob = this.InstrumentClient(container.GetBlockBlobClient(this.GetNewBlobName()));

                // Act
                var response = await blob.GetAccountInfoAsync();

                // Assert
                Assert.IsNotNull(response.GetRawResponse().Headers.RequestId);
            }
        }

        [Test]
        public async Task GetAccountInfoAsync_Error()
        {
            // Arrange
            var service = this.InstrumentClient(
                new BlobServiceClient(
                    this.GetServiceClient_SharedKey().Uri,
                    this.GetOptions()));
            var container = this.InstrumentClient(service.GetBlobContainerClient(this.GetNewContainerName()));
            var blob = this.InstrumentClient(container.GetBlockBlobClient(this.GetNewBlobName()));

            // Act
            await TestHelper.AssertExpectedExceptionAsync<StorageRequestFailedException>(
                blob.GetAccountInfoAsync(),
                e => Assert.AreEqual("ResourceNotFound", e.ErrorCode));
    }

        [Test]
        public async Task GetPropertiesAsync()
        {
            using (this.GetNewContainer(out var container))
            {
                // Arrange
                var blob = await this.GetNewBlobClient(container);

                // Act
                var response = await blob.GetPropertiesAsync();

                // Assert
                Assert.IsNotNull(response.GetRawResponse().Headers.RequestId);
            }
        }

        [Test]
        public async Task GetPropertiesAsync_ContainerSAS()
        {
            var containerName = this.GetNewContainerName();
            var blobName = this.GetNewBlobName();
            using (this.GetNewContainer(out var container, containerName: containerName))
            {
                // Arrange
                var blob = await this.GetNewBlobClient(container, blobName);

                var sasBlob = this.InstrumentClient(
                    this.GetServiceClient_BlobServiceSas_Container(
                        containerName: containerName)
                    .GetBlobContainerClient(containerName)
                    .GetBlockBlobClient(blobName));

                // Act
                var response = await sasBlob.GetPropertiesAsync();

                // Assert
                Assert.IsNotNull(response.GetRawResponse().Headers.RequestId);
            }
        }

        [Test]
        public async Task GetPropertiesAsync_ContainerIdentitySAS()
        {
            var oauthService = await this.GetServiceClient_OauthAccount();
            var containerName = this.GetNewContainerName();
            var blobName = this.GetNewBlobName();
            using (this.GetNewContainer(out var container, containerName: containerName, service: oauthService))
            {
                // Arrange
                var blob = await this.GetNewBlobClient(container, blobName);

                var userDelegationKey = await oauthService.GetUserDelegationKeyAsync(
                    start: null,
                    expiry: this.Recording.UtcNow.AddHours(1));

                var identitySasBlob = this.InstrumentClient(
                    this.GetServiceClient_BlobServiceIdentitySas_Container(
                        containerName: containerName,
                        userDelegationKey: userDelegationKey)
                    .GetBlobContainerClient(containerName)
                    .GetBlockBlobClient(blobName));

                // Act
                var response = await identitySasBlob.GetPropertiesAsync();

                // Assert
                Assert.IsNotNull(response.GetRawResponse().Headers.RequestId);
            }
        }

        [Test]
        public async Task GetPropertiesAsync_BlobSAS()
        {
            var containerName = this.GetNewContainerName();
            var blobName = this.GetNewBlobName();
            using (this.GetNewContainer(out var container, containerName: containerName))
            {
                // Arrange
                var blob = await this.GetNewBlobClient(container, blobName);

                var sasBlob = this.InstrumentClient(
                    this.GetServiceClient_BlobServiceSas_Blob(
                        containerName: containerName,
                        blobName: blobName)
                    .GetBlobContainerClient(containerName)
                    .GetBlockBlobClient(blobName));

                // Act
                var response = await sasBlob.GetPropertiesAsync();

                // Assert
                Assert.IsNotNull(response.GetRawResponse().Headers.RequestId);
            }
        }

        [Test]
        public async Task GetPropertiesAsync_BlobIdentitySAS()
        {
            var oauthService = await this.GetServiceClient_OauthAccount();
            var containerName = this.GetNewContainerName();
            var blobName = this.GetNewBlobName();
            using (this.GetNewContainer(out var container, containerName: containerName, service: oauthService))
            {
                // Arrange
                var blob = await this.GetNewBlobClient(container, blobName);

                var userDelegationKey = await oauthService.GetUserDelegationKeyAsync(
                    start: null,
                    expiry: this.Recording.UtcNow.AddHours(1));

                var identitySasBlob = this.InstrumentClient(
                    this.GetServiceClient_BlobServiceIdentitySas_Blob(
                        containerName: containerName,
                        blobName: blobName,
                        userDelegationKey: userDelegationKey)
                    .GetBlobContainerClient(containerName)
                    .GetBlockBlobClient(blobName));

                // Act
                var response = await identitySasBlob.GetPropertiesAsync();

                // Assert
                Assert.IsNotNull(response.GetRawResponse().Headers.RequestId);
            }
        }

        [Test]
        public async Task GetPropertiesAsync_SnapshotSAS()
        {
            var containerName = this.GetNewContainerName();
            var blobName = this.GetNewBlobName();
            using (this.GetNewContainer(out var container, containerName: containerName))
            {
                // Arrange
                var blob = await this.GetNewBlobClient(container, blobName);
                var snapshotResponse = await blob.CreateSnapshotAsync();

                var sasBlob = this.InstrumentClient(
                    this.GetServiceClient_BlobServiceSas_Snapshot(
                        containerName: containerName,
                        blobName: blobName,
                        snapshot: snapshotResponse.Value.Snapshot)
                    .GetBlobContainerClient(containerName)
                    .GetBlockBlobClient(blobName)
                    .WithSnapshot(snapshotResponse.Value.Snapshot));

                // Act
                var response = await sasBlob.GetPropertiesAsync();

                // Assert
                Assert.IsNotNull(response.GetRawResponse().Headers.RequestId);
            }
        }

        [Test]
        public async Task GetPropertiesAsync_SnapshotIdentitySAS()
        {
            var oauthService = await this.GetServiceClient_OauthAccount();
            var containerName = this.GetNewContainerName();
            var blobName = this.GetNewBlobName();
            using (this.GetNewContainer(out var container, containerName: containerName, service: oauthService))
            {
                // Arrange
                var blob = await this.GetNewBlobClient(container, blobName);
                var snapshotResponse = await blob.CreateSnapshotAsync();

                var userDelegationKey = await oauthService.GetUserDelegationKeyAsync(
                    start: null,
                    expiry: this.Recording.UtcNow.AddHours(1));

                var identitySasBlob = this.InstrumentClient(
                    this.GetServiceClient_BlobServiceIdentitySas_Container(
                        containerName: containerName,
                        userDelegationKey: userDelegationKey)
                    .GetBlobContainerClient(containerName)
                    .GetBlockBlobClient(blobName)
                    .WithSnapshot(snapshotResponse.Value.Snapshot));

                // Act
                var response = await identitySasBlob.GetPropertiesAsync();

                // Assert
                Assert.IsNotNull(response.GetRawResponse().Headers.RequestId);
            }
        }

        [Test]
        public async Task GetPropertiesAsync_AccessConditions()
        {
            var garbageLeaseId = this.GetGarbageLeaseId();
            foreach (var parameters in this.AccessConditions_Data)
            {
                using (this.GetNewContainer(out var container))
                {
                    // Arrange
                    var blob = await this.GetNewBlobClient(container);

                    parameters.Match = await this.SetupBlobMatchCondition(blob, parameters.Match);
                    parameters.LeaseId = await this.SetupBlobLeaseCondition(blob, parameters.LeaseId, garbageLeaseId);
                    var accessConditions = this.BuildAccessConditions(
                        parameters: parameters,
                        lease: true);

                    // Act
                    var response = await blob.GetPropertiesAsync(accessConditions: accessConditions);

                    // Assert
                    Assert.IsNotNull(response.GetRawResponse().Headers.RequestId);
                }
            }
        }

        [Test]
        public async Task GetPropertiesAsync_AccessConditionsFail()
        {
            var garbageLeaseId = this.GetGarbageLeaseId();
            foreach (var parameters in this.GetAccessConditionsFail_Data(garbageLeaseId))
            {
                using (this.GetNewContainer(out var container))
                {
                    // Arrange
                    var blob = await this.GetNewBlobClient(container);

                    parameters.NoneMatch = await this.SetupBlobMatchCondition(blob, parameters.NoneMatch);
                    var accessConditions = this.BuildAccessConditions(parameters);

                    // Act
                    await TestHelper.AssertExpectedExceptionAsync<StorageRequestFailedException>(
                        blob.GetPropertiesAsync(accessConditions: accessConditions),
                        e => { });
                }
            }
        }

        [Test]
        public async Task GetPropertiesAsync_Error()
        {
            using (this.GetNewContainer(out var container))
            {
                // Arrange
                var blob = this.InstrumentClient(container.GetBlockBlobClient(this.GetNewBlobName()));

                // Act
                await TestHelper.AssertExpectedExceptionAsync<StorageRequestFailedException>(
                    blob.GetPropertiesAsync(),
                    e => Assert.AreEqual("BlobNotFound", e.ErrorCode));
            }
        }

        [Test]
        public async Task SetHttpHeadersAsync()
        {
            var constants = new TestConstants(this);
            using (this.GetNewContainer(out var container))
            {
                // Arrange
                var blob = await this.GetNewBlobClient(container);

                // Act
                await blob.SetHttpHeadersAsync(new BlobHttpHeaders
                {
                    CacheControl = constants.CacheControl,
                    ContentDisposition = constants.ContentDisposition,
                    ContentEncoding = new string[] { constants.ContentEncoding },
                    ContentLanguage = new string[] { constants.ContentLanguage },
                    ContentHash = constants.ContentMD5,
                    ContentType = constants.ContentType
                });

                // Assert
                var response = await blob.GetPropertiesAsync();
                Assert.AreEqual(constants.ContentType, response.Value.ContentType);
                TestHelper.AssertSequenceEqual(constants.ContentMD5, response.Value.ContentHash);
                Assert.AreEqual(1, response.Value.ContentEncoding.Count());
                Assert.AreEqual(constants.ContentEncoding, response.Value.ContentEncoding.First());
                Assert.AreEqual(1, response.Value.ContentLanguage.Count());
                Assert.AreEqual(constants.ContentLanguage, response.Value.ContentLanguage.First());
                Assert.AreEqual(constants.ContentDisposition, response.Value.ContentDisposition);
                Assert.AreEqual(constants.CacheControl, response.Value.CacheControl);
            }
        }

        [Test]
        public async Task SetHttpHeadersAsync_AccessConditions()
        {
            var garbageLeaseId = this.GetGarbageLeaseId();
            foreach (var parameters in this.AccessConditions_Data)
            {
                using (this.GetNewContainer(out var container))
                {
                    // Arrange
                    var blob = await this.GetNewBlobClient(container);

                    parameters.Match = await this.SetupBlobMatchCondition(blob, parameters.Match);
                    parameters.LeaseId = await this.SetupBlobLeaseCondition(blob, parameters.LeaseId, garbageLeaseId);
                    var accessConditions = this.BuildAccessConditions(
                        parameters: parameters,
                        lease: true);

                    // Act
                    var response = await blob.SetHttpHeadersAsync(
                        httpHeaders: new BlobHttpHeaders(),
                        accessConditions: accessConditions);

                    // Assert
                    Assert.IsNotNull(response.GetRawResponse().Headers.RequestId);
                }
            }
        }

        [Test]
        public async Task SetHttpHeadersAsync_AccessConditionsFail()
        {
            var garbageLeaseId = this.GetGarbageLeaseId();
            foreach (var parameters in this.GetAccessConditionsFail_Data(garbageLeaseId))
            {
                using (this.GetNewContainer(out var container))
                {
                    // Arrange
                    var blob = await this.GetNewBlobClient(container);

                    parameters.NoneMatch = await this.SetupBlobMatchCondition(blob, parameters.NoneMatch);
                    var accessConditions = this.BuildAccessConditions(parameters);

                    // Act
                    await TestHelper.AssertExpectedExceptionAsync<StorageRequestFailedException>(
                        blob.SetHttpHeadersAsync(
                            httpHeaders: new BlobHttpHeaders(),
                            accessConditions: accessConditions),
                        e => { });
                }
            }
        }

        [Test]
        public async Task SetHttpHeadersAsync_Error()
        {
            using (this.GetNewContainer(out var container))
            {
                // Arrange
                var blob = this.InstrumentClient(container.GetBlockBlobClient(this.GetNewBlobName()));

                // Act
                await TestHelper.AssertExpectedExceptionAsync<StorageRequestFailedException>(
                    blob.SetHttpHeadersAsync(new BlobHttpHeaders()),
                    e => Assert.AreEqual("BlobNotFound", e.ErrorCode));
            }
        }

        [Test]
        public async Task SetMetadataAsync()
        {
            using (this.GetNewContainer(out var container))
            {
                // Arrange
                var blob = await this.GetNewBlobClient(container);
                var metadata = this.BuildMetadata();

                // Act
                await blob.SetMetadataAsync(metadata);

                // Assert
                var response = await blob.GetPropertiesAsync();
                this.AssertMetadataEquality(metadata, response.Value.Metadata);
            }
        }

        [Test]
        public async Task SetMetadataAsync_AccessConditions()
        {
            var garbageLeaseId = this.GetGarbageLeaseId();
            foreach (var parameters in this.AccessConditions_Data)
            {
                using (this.GetNewContainer(out var container))
                {
                    // Arrange
                    var blob = await this.GetNewBlobClient(container);
                    var metadata = this.BuildMetadata();

                    parameters.Match = await this.SetupBlobMatchCondition(blob, parameters.Match);
                    parameters.LeaseId = await this.SetupBlobLeaseCondition(blob, parameters.LeaseId, garbageLeaseId);
                    var accessConditions = this.BuildAccessConditions(
                        parameters: parameters,
                        lease: true);

                    // Act
                    var response = await blob.SetMetadataAsync(
                        metadata: metadata,
                        accessConditions: accessConditions);

                    // Assert
                    Assert.IsNotNull(response.GetRawResponse().Headers.RequestId);
                }
            }
        }

        [Test]
        public async Task SetMetadataAsync_AccessConditionsFail()
        {
            var garbageLeaseId = this.GetGarbageLeaseId();
            foreach (var parameters in this.GetAccessConditionsFail_Data(garbageLeaseId))
            {
                using (this.GetNewContainer(out var container))
                {
                    // Arrange
                    var blob = await this.GetNewBlobClient(container);
                    var metadata = this.BuildMetadata();

                    parameters.NoneMatch = await this.SetupBlobMatchCondition(blob, parameters.NoneMatch);
                    var accessConditions = this.BuildAccessConditions(parameters);

                    // Act
                    await TestHelper.AssertExpectedExceptionAsync<StorageRequestFailedException>(
                        blob.SetMetadataAsync(
                            metadata: metadata,
                            accessConditions: accessConditions),
                        e => { });
                }
            }
        }

        [Test]
        public async Task SetMetadataAsync_Error()
        {
            using (this.GetNewContainer(out var container))
            {
                // Arrange
                var blob = this.InstrumentClient(container.GetBlockBlobClient(this.GetNewBlobName()));
                var metadata = this.BuildMetadata();

                // Act
                await TestHelper.AssertExpectedExceptionAsync<StorageRequestFailedException>(
                    blob.SetMetadataAsync(metadata),
                    e => Assert.AreEqual("BlobNotFound", e.ErrorCode));
            }
        }

        [Test]
        public async Task CreateSnapshotAsync()
        {
            using (this.GetNewContainer(out var container))
            {
                // Arrange
                var blob = await this.GetNewBlobClient(container);

                // Act
                var response = await blob.CreateSnapshotAsync();

                // Assert
                Assert.IsNotNull(response.GetRawResponse().Headers.RequestId);
            }
        }

        [Test]
        public async Task CreateSnapshotAsync_AccessConditions()
        {
            var garbageLeaseId = this.GetGarbageLeaseId();
            foreach (var parameters in this.AccessConditions_Data)
            {
                using (this.GetNewContainer(out var container))
                {
                    // Arrange
                    var blob = await this.GetNewBlobClient(container);

                    parameters.Match = await this.SetupBlobMatchCondition(blob, parameters.Match);
                    parameters.LeaseId = await this.SetupBlobLeaseCondition(blob, parameters.LeaseId, garbageLeaseId);
                    var accessConditions = this.BuildAccessConditions(
                        parameters: parameters,
                        lease: true);

                    // Act
                    var response = await blob.CreateSnapshotAsync(accessConditions: accessConditions);

                    // Assert
                    Assert.IsNotNull(response.GetRawResponse().Headers.RequestId);
                }
            }
        }

        [Test]
        public async Task CreateSnapshotAsync_AccessConditionsFail()
        {
            var garbageLeaseId = this.GetGarbageLeaseId();
            foreach (var parameters in this.GetAccessConditionsFail_Data(garbageLeaseId))
            {
                using (this.GetNewContainer(out var container))
                {
                    // Arrange
                    var blob = await this.GetNewBlobClient(container);

                    parameters.NoneMatch = await this.SetupBlobMatchCondition(blob, parameters.NoneMatch);
                    var accessConditions = this.BuildAccessConditions(parameters);

                    // Act
                    await TestHelper.AssertExpectedExceptionAsync<StorageRequestFailedException>(
                        blob.CreateSnapshotAsync(accessConditions: accessConditions),
                        e => { });
                }
            }
        }

        [Test]
        public async Task CreateSnapshotAsync_Error()
        {
            using (this.GetNewContainer(out var container))
            {
                // Arrange
                var blob = this.InstrumentClient(container.GetBlockBlobClient(this.GetNewBlobName()));

                // Act
                await TestHelper.AssertExpectedExceptionAsync<StorageRequestFailedException>(
                    blob.CreateSnapshotAsync(),
                    e => Assert.AreEqual("BlobNotFound", e.ErrorCode));
            }
        }

        [Test]
        public async Task AcquireLeaseAsync()
        {
            using (this.GetNewContainer(out var container))
            {
                // Arrange
                var blob = await this.GetNewBlobClient(container);

                var leaseId = this.Recording.Random.NewGuid().ToString();
                var duration = 15;

                // Act
                var response = await blob.AcquireLeaseAsync(duration, leaseId);

                // Assert
                Assert.IsNotNull(response.GetRawResponse().Headers.RequestId);
            }
        }

        [Test]
        public async Task AcquireLeaseAsync_AccessConditions()
        {
            foreach (var parameters in this.NoLease_AccessConditions_Data)
            {
                using (this.GetNewContainer(out var container))
                {
                    // Arrange
                    var blob = await this.GetNewBlobClient(container);

                    var leaseId = this.Recording.Random.NewGuid().ToString();
                    var duration = 15;

                    parameters.Match = await this.SetupBlobMatchCondition(blob, parameters.Match);
                    var accessConditions = this.BuildHttpAccessConditions(
                        parameters: parameters);

                    // Act
                    var response = await blob.AcquireLeaseAsync(
                        duration: duration,
                        proposedID: leaseId,
                        httpAccessConditions: accessConditions);

                    // Assert
                    Assert.IsNotNull(response.GetRawResponse().Headers.RequestId);
                }
            }
        }

        [Test]
        public async Task AcquireLeaseAsync_AccessConditionsFail()
        {
            foreach (var parameters in this.NoLease_AccessConditionsFail_Data)
            {
                using (this.GetNewContainer(out var container))
                {
                    // Arrange
                    var blob = await this.GetNewBlobClient(container);

                    var leaseId = this.Recording.Random.NewGuid().ToString();
                    var duration = 15;

                    parameters.NoneMatch = await this.SetupBlobMatchCondition(blob, parameters.NoneMatch);
                    var accessConditions = this.BuildHttpAccessConditions(parameters);

                    // Act
                    await TestHelper.AssertExpectedExceptionAsync<StorageRequestFailedException>(
                        blob.AcquireLeaseAsync(
                            duration: duration,
                            proposedID: leaseId,
                            httpAccessConditions: accessConditions),
                        e => { });
                }
            }
        }

        [Test]
        public async Task AcquireLeaseAsync_Error()
        {
            using (this.GetNewContainer(out var container))
            {
                // Arrange
                var blob = this.InstrumentClient(container.GetBlockBlobClient(this.GetNewBlobName()));
                var leaseId = this.Recording.Random.NewGuid().ToString();
                var duration = 15;

                // Act
                await TestHelper.AssertExpectedExceptionAsync<StorageRequestFailedException>(
                    blob.AcquireLeaseAsync(duration, leaseId),
                    e => Assert.AreEqual("BlobNotFound", e.ErrorCode));
            }
        }

        [Test]
        public async Task RenewLeaseAsync()
        {
            using (this.GetNewContainer(out var container))
            {
                // Arrange
                var blob = await this.GetNewBlobClient(container);

                var leaseId = this.Recording.Random.NewGuid().ToString();
                var duration = 15;

                await blob.AcquireLeaseAsync(duration, leaseId);

                // Act
                var response = await blob.RenewLeaseAsync(leaseId);

                // Assert
                Assert.IsNotNull(response.GetRawResponse().Headers.RequestId);
            }
        }

        [Test]
        public async Task RenewLeaseAsync_AccessConditions()
        {
            foreach (var parameters in this.NoLease_AccessConditions_Data)
            {
                using (this.GetNewContainer(out var container))
                {
                    // Arrange
                    var blob = await this.GetNewBlobClient(container);

                    var leaseId = this.Recording.Random.NewGuid().ToString();
                    var duration = 15;

                    parameters.Match = await this.SetupBlobMatchCondition(blob, parameters.Match);
                    var accessConditions = this.BuildHttpAccessConditions(
                        parameters: parameters);

                    await blob.AcquireLeaseAsync(
                        duration: duration,
                        proposedID: leaseId);

                    // Act
                    var response = await blob.RenewLeaseAsync(
                        leaseID: leaseId,
                        httpAccessConditions: accessConditions);

                    // Assert
                    Assert.IsNotNull(response.GetRawResponse().Headers.RequestId);
                }
            }
        }

        [Test]
        public async Task RenewLeaseAsync_AccessConditionsFail()
        {
            foreach (var parameters in this.NoLease_AccessConditionsFail_Data)
            {
                using (this.GetNewContainer(out var container))
                {
                    // Arrange
                    var blob = await this.GetNewBlobClient(container);

                    var leaseId = this.Recording.Random.NewGuid().ToString();
                    var duration = 15;

                    parameters.NoneMatch = await this.SetupBlobMatchCondition(blob, parameters.NoneMatch);
                    var accessConditions = this.BuildHttpAccessConditions(parameters);

                    await blob.AcquireLeaseAsync(
                        duration: duration,
                        proposedID: leaseId);

                    // Act
                    await TestHelper.AssertExpectedExceptionAsync<StorageRequestFailedException>(
                        blob.RenewLeaseAsync(
                            leaseID: leaseId,
                            httpAccessConditions: accessConditions),
                        e => { });
                }
            }
        }

        [Test]
        public async Task RenewLeaseAsync_Error()
        {
            using (this.GetNewContainer(out var container))
            {
                // Arrange
                var blob = this.InstrumentClient(container.GetBlockBlobClient(this.GetNewBlobName()));
                var leaseId = this.Recording.Random.NewGuid().ToString();

                // Act
                await TestHelper.AssertExpectedExceptionAsync<StorageRequestFailedException>(
                    blob.ReleaseLeaseAsync(leaseId),
                    e => Assert.AreEqual("BlobNotFound", e.ErrorCode));
            }
        }

        [Test]
        public async Task ReleaseLeaseAsync()
        {
            using (this.GetNewContainer(out var container))
            {
                // Arrange
                var blob = await this.InstrumentClient(this.GetNewBlobClient(container));

                var leaseId = this.Recording.Random.NewGuid().ToString();
                var duration = 15;

                await blob.AcquireLeaseAsync(duration, leaseId);

                // Act
                var response = await blob.ReleaseLeaseAsync(leaseId);

                // Assert
                Assert.IsNotNull(response.GetRawResponse().Headers.RequestId);
            }
        }

        [Test]
        public async Task ReleaseLeaseAsync_AccessConditions()
        {
            foreach (var parameters in this.NoLease_AccessConditions_Data)
            {
                using (this.GetNewContainer(out var container))
                {
                    // Arrange
                    var blob = await this.GetNewBlobClient(container);

                    var leaseId = this.Recording.Random.NewGuid().ToString();
                    var duration = 15;

                    parameters.Match = await this.SetupBlobMatchCondition(blob, parameters.Match);
                    var accessConditions = this.BuildHttpAccessConditions(
                        parameters: parameters);

                    await blob.AcquireLeaseAsync(
                        duration: duration,
                        proposedID: leaseId);

                    // Act
                    var response = await blob.ReleaseLeaseAsync(
                        leaseID: leaseId,
                        httpAccessConditions: accessConditions);

                    // Assert
                    Assert.IsNotNull(response.GetRawResponse().Headers.RequestId);
                }
            }
        }

        [Test]
        public async Task ReleaseLeaseAsync_AccessConditionsFail()
        {
            foreach (var parameters in this.NoLease_AccessConditionsFail_Data)
            {
                using (this.GetNewContainer(out var container))
                {
                    // Arrange
                    var blob = await this.GetNewBlobClient(container);

                    var leaseId = this.Recording.Random.NewGuid().ToString();
                    var duration = 15;

                    parameters.NoneMatch = await this.SetupBlobMatchCondition(blob, parameters.NoneMatch);
                    var accessConditions = this.BuildHttpAccessConditions(parameters);

                    await blob.AcquireLeaseAsync(
                        duration: duration,
                        proposedID: leaseId);

                    // Act
                    await TestHelper.AssertExpectedExceptionAsync<StorageRequestFailedException>(
                        blob.ReleaseLeaseAsync(
                            leaseID: leaseId,
                            httpAccessConditions: accessConditions),
                        e => { });
                }
            }
        }

        [Test]
        public async Task ReleaseLeaseAsync_Error()
        {
            using (this.GetNewContainer(out var container))
            {
                // Arrange
                var blob = this.InstrumentClient(container.GetBlockBlobClient(this.GetNewBlobName()));
                var leaseId = this.Recording.Random.NewGuid().ToString();

                // Act
                await TestHelper.AssertExpectedExceptionAsync<StorageRequestFailedException>(
                    blob.RenewLeaseAsync(leaseId),
                    e => Assert.AreEqual("BlobNotFound", e.ErrorCode));
            }
        }

        [Test]
        public async Task BreakLeaseAsync()
        {
            using (this.GetNewContainer(out var container))
            {
                // Arrange
                var blob = await this.GetNewBlobClient(container);

                var leaseId = this.Recording.Random.NewGuid().ToString();
                var duration = 15;

                await blob.AcquireLeaseAsync(duration, leaseId);

                // Act
                var response = await blob.BreakLeaseAsync();

                // Assert
                Assert.IsNotNull(response.GetRawResponse().Headers.RequestId);
            }
        }

        [Test]
        public async Task BreakLeaseAsync_BreakPeriod()
        {
            using (this.GetNewContainer(out var container))
            {
                // Arrange
                var blob = await this.GetNewBlobClient(container);

                var leaseId = this.Recording.Random.NewGuid().ToString();
                var duration = 15;
                var breakPeriod = 5;

                await blob.AcquireLeaseAsync(duration, leaseId);

                // Act
                var response = await blob.BreakLeaseAsync(breakPeriodInSeconds: breakPeriod);

                // Assert
                Assert.IsNotNull(response.GetRawResponse().Headers.RequestId);
            }
        }

        [Test]
        public async Task BreakLeaseAsync_AccessConditions()
        {
            foreach (var parameters in this.NoLease_AccessConditions_Data)
            {
                using (this.GetNewContainer(out var container))
                {
                    // Arrange
                    var blob = await this.GetNewBlobClient(container);

                    var leaseId = this.Recording.Random.NewGuid().ToString();
                    var duration = 15;

                    parameters.Match = await this.SetupBlobMatchCondition(blob, parameters.Match);
                    var accessConditions = this.BuildHttpAccessConditions(
                        parameters: parameters);

                    await blob.AcquireLeaseAsync(
                        duration: duration,
                        proposedID: leaseId);

                    // Act
                    var response = await blob.BreakLeaseAsync(
                        httpAccessConditions: accessConditions);

                    // Assert
                    Assert.IsNotNull(response.GetRawResponse().Headers.RequestId);
                }
            }
        }

        [Test]
        public async Task BreakLeaseAsync_AccessConditionsFail()
        {
            foreach (var parameters in this.NoLease_AccessConditionsFail_Data)
            {
                using (this.GetNewContainer(out var container))
                {
                    // Arrange
                    var blob = await this.GetNewBlobClient(container);

                    var leaseId = this.Recording.Random.NewGuid().ToString();
                    var duration = 15;

                    parameters.NoneMatch = await this.SetupBlobMatchCondition(blob, parameters.NoneMatch);
                    var accessConditions = this.BuildHttpAccessConditions(parameters);

                    await blob.AcquireLeaseAsync(
                        duration: duration,
                        proposedID: leaseId);

                    // Act
                    await TestHelper.AssertExpectedExceptionAsync<StorageRequestFailedException>(
                        blob.BreakLeaseAsync(
                            httpAccessConditions: accessConditions),
                        e => { });
                }
            }
        }

        [Test]
        public async Task BreakLeaseAsync_Error()
        {
            using (this.GetNewContainer(out var container))
            {
                // Arrange
                var blob = this.InstrumentClient(container.GetBlockBlobClient(this.GetNewBlobName()));

                // Act
                await TestHelper.AssertExpectedExceptionAsync<StorageRequestFailedException>(
                    blob.BreakLeaseAsync(),
                    e => Assert.AreEqual("BlobNotFound", e.ErrorCode));
            }
        }

        [Test]
        public async Task ChangeLeaseAsync()
        {
            using (this.GetNewContainer(out var container))
            {
                // Arrange
                var blob = await this.GetNewBlobClient(container);

                var leaseId = this.Recording.Random.NewGuid().ToString();
                var newLeaseId = this.Recording.Random.NewGuid().ToString();
                var duration = 15;

                await blob.AcquireLeaseAsync(duration, leaseId);

                // Act
                var response = await blob.ChangeLeaseAsync(leaseId, newLeaseId);

                // Assert
                Assert.IsNotNull(response.GetRawResponse().Headers.RequestId);
            }
        }

        [Test]
        public async Task ChangeLeaseAsync_AccessConditions()
        {
            foreach (var parameters in this.NoLease_AccessConditions_Data)
            {
                using (this.GetNewContainer(out var container))
                {
                    // Arrange
                    var blob = await this.GetNewBlobClient(container);

                    var leaseId = this.Recording.Random.NewGuid().ToString();
                    var newLeaseId = this.Recording.Random.NewGuid().ToString();
                    var duration = 15;

                    parameters.Match = await this.SetupBlobMatchCondition(blob, parameters.Match);
                    var accessConditions = this.BuildHttpAccessConditions(
                        parameters: parameters);

                    await blob.AcquireLeaseAsync(
                        duration: duration,
                        proposedID: leaseId);

                    // Act
                    var response = await blob.ChangeLeaseAsync(
                        leaseID: leaseId,
                        proposedID: newLeaseId,
                        httpAccessConditions: accessConditions);

                    // Assert
                    Assert.IsNotNull(response.GetRawResponse().Headers.RequestId);
                }
            }
        }

        [Test]
        public async Task ChangeLeaseAsync_AccessConditionsFail()
        {
            foreach (var parameters in this.NoLease_AccessConditionsFail_Data)
            {
                using (this.GetNewContainer(out var container))
                {
                    // Arrange
                    var blob = await this.GetNewBlobClient(container);

                    var leaseId = this.Recording.Random.NewGuid().ToString();
                    var newLeaseId = this.Recording.Random.NewGuid().ToString();
                    var duration = 15;

                    parameters.NoneMatch = await this.SetupBlobMatchCondition(blob, parameters.NoneMatch);
                    var accessConditions = this.BuildHttpAccessConditions(parameters);

                    await blob.AcquireLeaseAsync(
                        duration: duration,
                        proposedID: leaseId);

                    // Act
                    await TestHelper.AssertExpectedExceptionAsync<StorageRequestFailedException>(
                        blob.ChangeLeaseAsync(
                            leaseID: leaseId,
                            proposedID: newLeaseId,
                            httpAccessConditions: accessConditions),
                        e => { });
                }
            }
        }

        [Test]
        public async Task ChangeLeaseAsync_Error()
        {
            using (this.GetNewContainer(out var container))
            {
                // Arrange
                var blob = this.InstrumentClient(container.GetBlockBlobClient(this.GetNewBlobName()));
                var leaseId = this.Recording.Random.NewGuid().ToString();
                var newLeaseId = this.Recording.Random.NewGuid().ToString();

                // Act
                await TestHelper.AssertExpectedExceptionAsync<StorageRequestFailedException>(
                    blob.ChangeLeaseAsync(
                        leaseID: leaseId,
                        proposedID: newLeaseId),
                    e => Assert.AreEqual("BlobNotFound", e.ErrorCode));
            }
        }

        [Test]
        public async Task SetTierAsync()
        {
            using (this.GetNewContainer(out var container))
            {
                // Arrange
                var blob = await this.GetNewBlobClient(container);

                // Act
                var response = await blob.SetTierAsync(AccessTier.Cool);

                // Assert
                Assert.IsNotNull(response.Headers.RequestId);
            }
        }

        [Test]
        public async Task SetTierAsync_Lease()
        {
            using (this.GetNewContainer(out var container))
            {
                // Arrange
                var blob = await this.GetNewBlobClient(container);

                var leaseId = this.Recording.Random.NewGuid().ToString();
                var duration = 15;

                await blob.AcquireLeaseAsync(duration, leaseId);

                // Act
                var response = await blob.SetTierAsync(
                    accessTier: AccessTier.Cool, 
                    leaseAccessConditions: new LeaseAccessConditions
                    {
                        LeaseId = leaseId
                    });

                // Assert
                Assert.IsNotNull(response.Headers.RequestId);
            }
        }

        [Test]
        public async Task SetTierAsync_LeaseFail()
        {
            using (this.GetNewContainer(out var container))
            {
                // Arrange
                var data = this.GetRandomBuffer(Constants.KB);

                var blob = this.InstrumentClient(container.GetBlockBlobClient(this.GetNewBlobName()));
                using (var stream = new MemoryStream(data))
                {
                    await blob.UploadAsync(stream);
                }

                var leaseId = this.Recording.Random.NewGuid().ToString();

                // Act
                await this.AssertExpectedExceptionAsync<StorageRequestFailedException, Response>(
                    blob.SetTierAsync(
                        accessTier: AccessTier.Cool,
                        leaseAccessConditions: new LeaseAccessConditions
                        {
                            LeaseId = leaseId
                        }),
                    e => Assert.AreEqual("LeaseNotPresentWithBlobOperation", e.ErrorCode));
            }
        }

        [Test]
        public async Task SetTierAsync_Error()
        {
            using (this.GetNewContainer(out var container))
            {
                // Arrange
                var blob = this.InstrumentClient(container.GetBlockBlobClient(this.GetNewBlobName()));
                var leaseId = this.Recording.Random.NewGuid().ToString();
                var newLeaseId = this.Recording.Random.NewGuid().ToString();

                // Act
                await this.AssertExpectedExceptionAsync<StorageRequestFailedException, Response>(
                    blob.SetTierAsync(AccessTier.Cool),
                    e => Assert.AreEqual("BlobNotFound", e.ErrorCode));
            }
        }

        //[Test]
        //public async Task SetTierAsync_Batch()
        //{
        //    using (this.GetNewContainer(out var container, service: this.GetServiceClient_PreviewAccount_SharedKey()))
        //    {
        //        const int blobSize = Constants.KB;
        //        var data = this.GetRandomBuffer(blobSize);

        //        var blob1 = this.InstrumentClient(container.CreateBlockBlobClient(this.GetNewBlobName()));
        //        using (var stream = new MemoryStream(data))
        //        {
        //            await blob1.UploadAsync(stream);
        //        }

        //        var blob2 = this.InstrumentClient(container.CreateBlockBlobClient(this.GetNewBlobName()));
        //        using (var stream = new MemoryStream(data))
        //        {
        //            await blob2.UploadAsync(stream);
        //        }

        //        var batch =
        //            blob1.SetTierAsync(AccessTier.Cool)
        //            .And(blob2.SetTierAsync(AccessTier.Cool))
        //            ;

        //        var result = await batch;

        //        Assert.IsNotNull(result);
        //        Assert.AreEqual(2, result.Length);
        //        Assert.IsNotNull(result[0].RequestId);
        //        Assert.IsNotNull(result[1].RequestId);
        //    }
        //}

        [Test]
        public void WithSnapshot()
        {
            var containerName = this.GetNewContainerName();
            var blobName = this.GetNewBlobName();

            var service = this.GetServiceClient_SharedKey();

            var container = this.InstrumentClient(service.GetBlobContainerClient(containerName));

            var blob = this.InstrumentClient(container.GetBlockBlobClient(blobName));

            var builder = new BlobUriBuilder(blob.Uri);

            Assert.AreEqual("", builder.Snapshot);

            blob = this.InstrumentClient(blob.WithSnapshot("foo"));

            builder = new BlobUriBuilder(blob.Uri);

            Assert.AreEqual("foo", builder.Snapshot);

            blob = this.InstrumentClient(blob.WithSnapshot(null));

            builder = new BlobUriBuilder(blob.Uri);

            Assert.AreEqual("", builder.Snapshot);
        }

        private async Task<BlobClient> GetNewBlobClient(BlobContainerClient container, string blobName = default)
        {
            blobName = blobName ?? this.GetNewBlobName();
            var blob = this.InstrumentClient(container.GetBlockBlobClient(blobName));
            var data = this.GetRandomBuffer(Constants.KB);

            using (var stream = new MemoryStream(data))
            {
                await blob.UploadAsync(stream);
            }
            return blob;
        }

        public IEnumerable<AccessConditionParameters> AccessConditions_Data
            => new[]
            {
                new AccessConditionParameters(),
                new AccessConditionParameters { IfModifiedSince = this.OldDate },
                new AccessConditionParameters { IfUnmodifiedSince = this.NewDate },
                new AccessConditionParameters { Match = this.ReceivedETag },
                new AccessConditionParameters { NoneMatch = this.GarbageETag },
                new AccessConditionParameters { LeaseId = this.ReceivedLeaseId }
            };

        public IEnumerable<AccessConditionParameters> GetAccessConditionsFail_Data(string garbageLeaseId)
            => new[]
            {
                new AccessConditionParameters { IfModifiedSince = this.NewDate },
                new AccessConditionParameters { IfUnmodifiedSince = this.OldDate },
                new AccessConditionParameters { Match = this.GarbageETag },
                new AccessConditionParameters { NoneMatch = this.ReceivedETag },
                new AccessConditionParameters { LeaseId = garbageLeaseId },
             };

        public IEnumerable<AccessConditionParameters> NoLease_AccessConditions_Data
            => new[]
            {
                new AccessConditionParameters(),
                new AccessConditionParameters { IfModifiedSince = this.OldDate },
                new AccessConditionParameters { IfUnmodifiedSince = this.NewDate },
                new AccessConditionParameters { Match = this.ReceivedETag },
                new AccessConditionParameters { NoneMatch = this.GarbageETag },
            };

        public IEnumerable<AccessConditionParameters> NoLease_AccessConditionsFail_Data
            => new[]
            {
                new AccessConditionParameters { IfModifiedSince = this.NewDate },
                new AccessConditionParameters { IfUnmodifiedSince = this.OldDate },
                new AccessConditionParameters { Match = this.GarbageETag },
                new AccessConditionParameters { NoneMatch = this.ReceivedETag },
            };

        private HttpAccessConditions BuildHttpAccessConditions(
            AccessConditionParameters parameters)
            => new HttpAccessConditions
            {
                IfModifiedSince = parameters.IfModifiedSince,
                IfUnmodifiedSince = parameters.IfUnmodifiedSince,
                IfMatch = parameters.Match != null ? new ETag(parameters.Match) : default(ETag?),
                IfNoneMatch = parameters.NoneMatch != null ? new ETag(parameters.NoneMatch) : default(ETag?)
            };

        private BlobAccessConditions BuildAccessConditions(
            AccessConditionParameters parameters,
            bool lease = true)
        {
            var accessConditions = new BlobAccessConditions
            {
                HttpAccessConditions = this.BuildHttpAccessConditions(parameters)
            };
            if(lease)
            {
                accessConditions.LeaseAccessConditions = new LeaseAccessConditions
                {
                    LeaseId = parameters.LeaseId
                };
            }
            return accessConditions;
        }

        public class AccessConditionParameters
        {
            public DateTimeOffset? IfModifiedSince { get; set; }
            public DateTimeOffset? IfUnmodifiedSince { get; set; }
            public string Match { get; set; }
            public string NoneMatch { get; set; }
            public string LeaseId { get; set; }
        }
    }
}
