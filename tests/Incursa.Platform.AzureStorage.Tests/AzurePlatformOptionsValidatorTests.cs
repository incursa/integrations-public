// Copyright (c) Incursa
//
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//
//     http://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.

using Microsoft.Extensions.Options;
using Shouldly;

namespace Incursa.Platform.AzureStorage.Tests;

[Trait("Category", "Unit")]
public sealed class AzurePlatformOptionsValidatorTests
{
    [Fact]
    public void Validate_WithConnectionStringOnly_Succeeds()
    {
        AzurePlatformOptions options = AzurePlatformTestOptions.CreateUnitOptions();
        AzurePlatformOptionsValidator validator = new();

        ValidateOptionsResult result = validator.Validate(Options.DefaultName, options);

        result.Succeeded.ShouldBeTrue();
    }

    [Fact]
    public void Validate_WithManagedIdentityUris_Succeeds()
    {
        AzurePlatformOptions options = AzurePlatformTestOptions.CreateUnitOptions();
        options.ConnectionString = null;
        options.BlobServiceUri = new Uri("https://example.blob.core.windows.net/");
        options.QueueServiceUri = new Uri("https://example.queue.core.windows.net/");
        options.TableServiceUri = new Uri("https://example.table.core.windows.net/");
        AzurePlatformOptionsValidator validator = new();

        ValidateOptionsResult result = validator.Validate(Options.DefaultName, options);

        result.Succeeded.ShouldBeTrue();
    }

    [Fact]
    public void Validate_WithMissingStorageSettings_Fails()
    {
        AzurePlatformOptions options = AzurePlatformTestOptions.CreateUnitOptions();
        options.ConnectionString = null;
        options.BlobServiceUri = null;
        options.QueueServiceUri = null;
        options.TableServiceUri = null;
        AzurePlatformOptionsValidator validator = new();

        ValidateOptionsResult result = validator.Validate(Options.DefaultName, options);

        result.Failed.ShouldBeTrue();
        result.Failures.ShouldContain(failure => failure.Contains("Provide either a connection string", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_WithInvalidQueueName_Fails()
    {
        AzurePlatformOptions options = AzurePlatformTestOptions.CreateUnitOptions();
        options.OutboxSignalQueueName = "Bad_Queue";
        AzurePlatformOptionsValidator validator = new();

        ValidateOptionsResult result = validator.Validate(Options.DefaultName, options);

        result.Failed.ShouldBeTrue();
    }
}
