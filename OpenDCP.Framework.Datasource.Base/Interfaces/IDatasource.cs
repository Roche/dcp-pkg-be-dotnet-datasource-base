using OpenDCP.Framework.Configuration.Model.Constants.Datasource;
using OpenDCP.Framework.Configuration.Model.Entities.Datasource;
using OpenDCP.Framework.FormBuilder.Models;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace OpenDCP.Framework.Datasource.Base.Interfaces
{
    public interface IDatasource
    {
        List<string> GetSecretFields();
        List<DataType> GetImplementedDataTypes();
        string DisplayConnectionString();
        Task ValidateConnectionString(JsonObject connectionString);
        JsonObject TransformConnectionString(List<FormValue> connectionString, JsonObject? previousConnString = null);
        string GetEncryptedConnectionStringAsync(JsonObject connectionString, string key);
        List<InputField> GetConnectionStringConfigurationForm();
        JsonObject BuildsDatasourceObject(DatasourceDTO datasourceDetails);
    }
}
