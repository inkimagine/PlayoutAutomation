﻿using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using TAS.Server.Interfaces;
using TAS.Server.Remoting;

namespace TAS.Client.Server.Remoting
{
    public class DtoSerializationConverter : JsonConverter
    {
        private readonly Type iDtoType = typeof(IDto);
        private readonly ConcurrentDictionary<Guid, IDto> _dtos = new ConcurrentDictionary<Guid, IDto>();
        public override bool CanConvert(Type objectType)
        {
            return iDtoType.IsAssignableFrom(objectType);
        }

        public override object ReadJson(JsonReader reader, Type objectType, object existingValue, JsonSerializer serializer)
        {
            object deserialized = new ReceivedDto();
            serializer.Populate(reader, deserialized);
            if (deserialized != null)
            {
                IDto oldObject;
                if (_dtos.TryGetValue(((IDto)deserialized).DtoGuid, out oldObject))
                    return oldObject;
            }
            throw new ApplicationException("DtoSerializationConverter: Dto not found");
        }

        public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer)
        {
            JToken t = JToken.FromObject(value);
            t.WriteTo(writer);
            IDto dto = value as IDto;
            if (dto != null)
                _dtos[dto.DtoGuid] = dto;
        }

        public void Clear()
        {
            _dtos.Clear();
        }

        public bool TryGetValue(Guid guid, out IDto value)
        {
            return _dtos.TryGetValue(guid, out value);
        }

        public bool TryRemove(Guid guid, out IDto value)
        {
            return _dtos.TryRemove(guid, out value);
        }
    }
}