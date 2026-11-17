using Microsoft.Extensions.Configuration;
using OpenDCP.Framework.Configuration.Model.Constants.Datasource;
using OpenDCP.Framework.Configuration.Model.Entities.Datasource;
using OpenDCP.Framework.Configuration.Model.Extensions;
using OpenDCP.Framework.Datasource.Base.Constants;
using OpenDCP.Framework.Exceptions;
using OpenDCP.Framework.FormBuilder.Constants;
using OpenDCP.Framework.FormBuilder.Extensions;
using OpenDCP.Framework.FormBuilder.Models;
using OpenDCP.Framework.Security.Classes;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using DataType = OpenDCP.Framework.Configuration.Model.Constants.Datasource.DataType;

namespace OpenDCP.Framework.Datasource.Base
{
    public abstract class BaseDatasource
    {
        protected abstract List<InputField> ConnectionStringFields { get; }
        [Obsolete("This property is deprecated. Use ConnectionType instead.")]
        protected abstract ConnectionType Type { get; }
        public static string ConnectionType => "Unknown";
        protected abstract List<string> SecretFields { get; }
        protected abstract List<string> SecretDistributionFields { get; }
        protected JsonObject? _connectionString { get; set; } = null;
        protected int? _id { get; set; }
        protected IConfiguration _configuration { get; set; }
        public BaseDatasource(int? id, JsonObject connectionString, IConfiguration configuration)
        {
            _id = id;
            _connectionString = connectionString;
            _configuration = configuration;
        }
        public BaseDatasource(IConfiguration configuration)
        {
            _configuration = configuration;
        }
        public abstract List<DataType> GetImplementedDataTypes();
        public List<string> GetSecretFields()
        {
            return SecretFields;
        }
        public string GetEncryptedConnectionStringAsync(string key)
        {
            return EncryptionHelper.EncryptConnectionString(_connectionString, key);
        }
        public List<InputField> GetConnectionStringConfigurationForm()
        {
            var result = ConnectionStringFields;

            //Apply the defaults of the current connection string without exposing the secrets
            if (_connectionString != null)
            {
                foreach (var field in result)
                {
                    if (SecretFields.Contains(field.Name, StringComparer.OrdinalIgnoreCase))
                    {
                        //This is a secret field, values are not revealed
                        //In order the flow to work the required validator needs to be removed
                        field.FieldValidations = field.FieldValidations
                            .Where(x => x.Type != InputFieldValidationType.Required.ToString())
                            .ToList();
                        continue;
                    }

                    if (field.Children != null && field.Children.Any() && field.FieldType == (byte)InputFieldType.Group)
                    {
                        var parentValue = _connectionString[field.Name];

                        if (parentValue is JsonObject parent)
                        {
                            foreach (var child in field.Children)
                            {
                                if (SecretFields.Contains(child.Name, StringComparer.OrdinalIgnoreCase))
                                {
                                    //This is a secret field, values are not revealed
                                    //In order the flow to work the required validator needs to be removed
                                    child.FieldValidations = child.FieldValidations
                                        .Where(x => x.Type != InputFieldValidationType.Required.ToString())
                                        .ToList();
                                    continue;
                                }

                                if (parent.Any(kvp => kvp.Key.Equals(child.Name, StringComparison.OrdinalIgnoreCase)))
                                {
                                    if (child.FieldSettings == null)
                                    {
                                        child.FieldSettings = new FieldSettings();
                                    }

                                    // value from connection string
                                    var valueFromConnection = ParseValue(parent[child.Name]);

                                    // only overwrite if connection string actually has a value
                                    if (valueFromConnection != null)
                                    {
                                        child.FieldSettings.DefaultValue = valueFromConnection;
                                    }
                                }
                            }
                        }
                    }
                    else
                    {
                        if (field.FieldSettings == null)
                        {
                            field.FieldSettings = new FieldSettings();
                        }

                        // value from connection string
                        var valueFromConnection = ParseValue(_connectionString[field.Name]);

                        // only overwrite if connection string actually has a value
                        if (valueFromConnection != null)
                        {
                            field.FieldSettings.DefaultValue = valueFromConnection;
                        }
                    }
                }
            }

            return result;
        }
        public void UpdateConnectionString(List<FormValue> connectionString)
        {
            if (_connectionString == null)
            {
                _connectionString = new JsonObject();
            }

            foreach (var field in ConnectionStringFields)
            {
                var formValue = connectionString
                    .FirstOrDefault(v => v.Key.Equals(field.Name, StringComparison.OrdinalIgnoreCase));

                if (formValue == null)
                {
                    //Value is not mandatory, add as null
                    if (!field.FieldValidations.Any(val => val.Type == InputFieldValidationType.Required.ToName()))
                    {
                        _connectionString[field.Name] = null;
                    }
                    else
                    {
                        //The field is mandatory and not a secret so the previous value is used
                        if (!SecretFields.Contains(field.Name, StringComparer.OrdinalIgnoreCase)
                            || !_connectionString.Any(kvp => kvp.Key.Equals(field.Name, StringComparison.OrdinalIgnoreCase)))
                        {
                            throw new Exception(ErrorMessages.MissingRequiredConnectionStringParameter);
                        }
                    }
                }
                else
                {
                    _connectionString[field.Name] = GetJsonElementBaseValue(formValue.Value);
                }
            }
        }

        public abstract string DisplayConnectionString();

        public JsonObject BuildsDatasourceObject(DatasourceDTO datasourceDetails)
        {
            if (!Enum.IsDefined(typeof(ConnectionType), (ConnectionType)datasourceDetails.Type))
            {
                throw new DCPBadRequestException("Datasource not supported type");
            }

            var datasourceType = (ConnectionType)datasourceDetails.Type;

            var result = new JsonObject
            {
                { "Name", datasourceDetails.Name },
                { "Type", datasourceType.ToName() },
                { "ConnectionString", DisplayConnectionString() }
            };

            return result;
        }

        protected string DataTypePromoting(List<string> dataTypes)
        {
            var nonNumericTypes = new List<string> { "GUID", "DateTime", "Byte", "String", "EnumerationValue", "Enum" };
            var numericTypes = new List<string> { "Double", "Single", "Int64", "Int32", "Int16" };

            var uniqueValues = dataTypes
                .Distinct()
                .ToList();

            var intersectNonNumeric = uniqueValues
                .Intersect(nonNumericTypes)
                .ToList();

            var intersectNumeric = numericTypes
                .Intersect(uniqueValues)
                .ToList();

            //If all datatypes are the same
            if (uniqueValues.Count == 1)
            {
                return dataTypes.First();
            }
            //Promoting to String if there is non numeric datatype
            else if (intersectNonNumeric.Count > 0)
            {
                return "String";
            }
            //Promoting to the highest numeric datatype
            else if (intersectNumeric.Count > 0)
            {
                return intersectNumeric.First();
            }
            else
            {
                throw new DCPBadRequestException(ErrorMessages.UnknownValueType);
            }
        }
        public virtual JsonObject TransformConnectionString(List<FormValue> connectionString, JsonObject? previousConnString = null)
        {
            JsonObject connString = new JsonObject();

            var currentConnString = ConvertFormValuesToJsonObject(connectionString);

            foreach (var field in ConnectionStringFields)
            {
                currentConnString.TryGetPropertyValue(field.Name, out var currentValue);
                JsonNode? previousValue = null;
                previousConnString?.TryGetPropertyValue(field.Name, out previousValue);

                connString[field.Name] = ProcessValue(field, currentValue, previousValue, currentConnString, null);
            }
            return connString;
        }

        private JsonObject ConvertFormValuesToJsonObject(List<FormValue> formValues)
        {
            var json = new JsonObject();

            foreach (var fv in formValues)
            {
                json[fv.Key] = ConvertDynamicToJsonNode(fv.Value);
            }

            return json;
        }

        private JsonNode? ConvertDynamicToJsonNode(object? value)
        {
            if (value is null)
                return null;

            if (value is JsonNode node)
                return node;

            if (value is JsonElement el)
            {
                if (el.ValueKind == JsonValueKind.Null)
                    return null;

                return JsonNode.Parse(el.GetRawText());
            }

            if (value is string ||
                value is bool ||
                value is int ||
                value is long ||
                value is double ||
                value is decimal)
            {
                return JsonValue.Create(value);
            }

            return JsonNode.Parse(JsonSerializer.Serialize(value));
        }

        private JsonNode? ProcessValue(InputField field, JsonNode? currentValue, JsonNode? previousValue, JsonObject connectionString,
            string? parentPath)
        {
            if (field.Children == null || !field.Children.Any())
            {
                if (currentValue is JsonValue val && val.GetValueKind() != JsonValueKind.Null)
                {
                    return val.DeepClone();
                }

                if (currentValue != null && currentValue.GetValueKind() != JsonValueKind.Null)
                {
                    return currentValue.DeepClone();
                }

                if (SecretFields.Contains(field.Name, StringComparer.OrdinalIgnoreCase) && field.IsVisible(connectionString, parentPath))
                {
                    if (previousValue != null)
                    {
                        return previousValue.DeepClone();
                    }

                    throw new ArgumentException($"Missing value for required secret field '{field.Name}'");
                }

                if (currentValue == null || currentValue.GetValueKind() == JsonValueKind.Null)
                {
                    if (!field.FieldValidations.Any(val => val.Type == InputFieldValidationType.Required.ToName()) || !field.IsVisible(connectionString, parentPath))
                    {
                        return null;
                    }

                    throw new ArgumentException("A mandatory field of the connection string is missing");
                }

                return null;
            }

            //JsonArray
            if (currentValue is JsonArray currArray)
            {
                var prevArray = previousValue as JsonArray;
                var resultArray = new JsonArray();

                for (int i = 0; i < currArray.Count; i++)
                {
                    var currItem = currArray[i];
                    var prevItem = prevArray != null && i < prevArray.Count
                        ? prevArray[i]
                        : null;

                    if (currItem is JsonValue || currItem is null)
                    {
                        resultArray.Add(ResolveArrayPrimitive(field, currItem, prevItem, connectionString));
                        continue;
                    }

                    if (currItem is JsonObject)
                    {
                        resultArray.Add(ProcessObjectNode(field, currItem, prevItem, connectionString));
                        continue;
                    }

                    resultArray.Add(currItem?.DeepClone());
                }

                return resultArray;
            }

            //JsonObject
            if (currentValue is JsonObject currObj)
            {
                var prevObj = previousValue as JsonObject;
                var resultObj = new JsonObject();

                foreach (var child in field.Children)
                {
                    currObj.TryGetPropertyValue(child.Name, out var currChild);
                    JsonNode? prevChild = null;
                    prevObj?.TryGetPropertyValue(child.Name, out prevChild);

                    resultObj[child.Name] =
                        ProcessValue(child, currChild, prevChild, connectionString, parentPath is null ? field.Name : $"{parentPath}.{field.Name}");
                }

                return resultObj;
            }

            if (currentValue == null || currentValue.GetValueKind() == JsonValueKind.Null)
            {
                if (!field.FieldValidations.Any(val => val.Type == InputFieldValidationType.Required.ToName()) || !field.IsVisible(connectionString, parentPath))
                {
                    return null;
                }

                throw new ArgumentException("A mandatory field of the connection string is missing");
            }

            return currentValue.DeepClone();
        }

        private JsonObject ProcessObjectNode(InputField field, JsonNode? currentValue, JsonNode? previousValue, JsonObject connectionString)
        {
            var result = new JsonObject();

            var currObj = currentValue as JsonObject;

            if (currObj == null || currObj.Count != 1)
            {
                throw new ArgumentException(
                    $"Array element for '{field.Name}' must be a JsonObject"
                );
            }

            var kvp = currObj.First();
            var childName = kvp.Key;
            var childValue = kvp.Value;

            var childField = field.Children?
                .FirstOrDefault(c =>
                    c.Name.Equals(childName, StringComparison.OrdinalIgnoreCase));

            if (childField == null)
            {
                throw new ArgumentException(
                    $"No matching child field '{childName}' found for array '{field.Name}'."
                );
            }

            JsonNode? prevChild = null;
            if (previousValue is JsonObject prevObj)
            {
                prevObj.TryGetPropertyValue(childName, out prevChild);
            }

            result[childName] = ProcessValue(childField, childValue, prevChild, connectionString, null);

            return result;
        }

        private JsonNode? ResolveArrayPrimitive(InputField field, JsonNode? currentValue, JsonNode? previousValue, JsonObject connectionString)
        {
            // value provided
            if (currentValue != null && currentValue.GetValueKind() != JsonValueKind.Null)
            {
                return currentValue.DeepClone();
            }

            // secret fallback
            if (SecretFields.Contains(field.Name, StringComparer.OrdinalIgnoreCase) && field.IsVisible(connectionString))
            {
                if (previousValue != null)
                {
                    return previousValue.DeepClone();
                }

                throw new Exception($"Missing value for required secret field '{field.Name}'");
            }

            return null;
        }

        public virtual string GetEncryptedConnectionStringAsync(JsonObject connectionString, string key)
        {
            return EncryptionHelper.EncryptConnectionString(connectionString, key);
        }
        protected dynamic? GetJsonElementBaseValue(JsonElement? element)
        {
            if (element == null)
            {
                return null;
            }

            return element.Value.ValueKind switch
            {
                JsonValueKind.String => element.Value.GetString(),
                JsonValueKind.Number => element.Value.TryGetInt64(out long l) ? l : element.Value.GetDouble(),
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.Null => null,
                JsonValueKind.Object => JsonNode.Parse(element.Value.GetRawText()),
                _ => throw new InvalidOperationException($"Unsupported JsonValueKind: {element.Value.ValueKind}")
            };
        }
        protected string ReadTemplate(string templateFilePath)
        {
            try
            {
                if (!File.Exists(templateFilePath))
                {
                    throw new FileNotFoundException($"The template file '{templateFilePath}' was not found.");
                }

                string fileContents = File.ReadAllText(templateFilePath);

                return fileContents;
            }
            catch (FileNotFoundException ex)
            {
                throw new DCPInternalServerErrorException(ex.Message);
            }
            catch (UnauthorizedAccessException ex)
            {
                throw new DCPForbiddenException(ex.Message);
            }
            catch (Exception ex)
            {
                throw new DCPInternalServerErrorException(ex.Message);
            }
        }
        protected string ReplaceTemplatePlaceholders(string skeleton, Dictionary<string, string> parameters)
        {
            foreach (var param in parameters)
            {
                skeleton = skeleton.Replace($"{{{{{param.Key}}}}}", param.Value);
            }
            return skeleton;
        }

        public static dynamic? ParseValue(dynamic? value)
        {
            if (value == null)
            {
                return null;
            }

            if (value is IEnumerable<object> enumerable)
            {
                var list = new List<dynamic>();
                foreach (var item in enumerable)
                {
                    list.Add(ParseValue(item));
                }
                return list;
            }

            if (value is JsonArray jsonArray)
            {
                return jsonArray;
            }

            if (value is JsonObject jsonObject)
            {
                return jsonObject;
            }

            if (value is bool || value is int || value is double || value is DateTime)
            {
                return value;
            }

            string? stringValue = Convert.ToString(value);

            if (bool.TryParse(stringValue, out bool boolVal))
            {
                return boolVal;
            }
            else if (int.TryParse(stringValue, out int intVal))
            {
                return intVal;
            }
            else if (double.TryParse(stringValue, out double doubleVal))
            {
                return doubleVal;
            }
            else if (DateTime.TryParse(stringValue, out DateTime dateVal))
            {
                return dateVal;
            }
            else
            {
                return value?.ToString();
            }
        }
    }
}
