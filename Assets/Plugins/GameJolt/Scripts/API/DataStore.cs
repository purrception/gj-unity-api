﻿using System;
using System.Collections.Generic;
using GameJolt.API.Core;

namespace GameJolt.API {
	/// <summary>
	/// DataStore operations.
	/// </summary>
	public enum DataStoreOperation {
		Add,
		Subtract,
		Multiply,
		Divide,
		Append,
		Prepend
	};

	/// <summary>
	/// The DataStore API methods.
	/// </summary>
	public static class DataStore {
		/// <summary>
		/// GameJolt refuses requests larger than this.
		/// In order for an operation to succeed the key length + value length must not be larger than this.
		/// </summary>
		public const int SoftLimit = 1024 * 1024 - 10;
		/// <summary>
		/// GameJolt allows only about 16MB per key-value pair.
		/// </summary>
		public const int HardLimit = 16777000; //~16MB

		/// <summary>
		/// Save the key/value pair in the DataStore.
		/// Caution: GameJolt refuses requests with a payload of more than 1MB.
		/// The payload is composed of the key, the value and 10 bytes of meta data.
		/// Use <see cref="SetSegmented"/> for data larger than 1MB.
		/// </summary>
		/// <param name="key">The key name.</param>
		/// <param name="value">The value to store.</param>
		/// <param name="global">A boolean indicating whether the key is global (<c>true</c>) or private to the user (<c>false</c>).</param>
		/// <param name="callback">A callback function accepting a single parameter, a boolean indicating success.</param>
		public static void Set(string key, string value, bool global, Action<bool> callback = null) {
			if(key.Length + value.Length > SoftLimit) {
				LogHelper.Error("Failed to upload data, because it was to large.");
				if(callback != null) callback(false);
				return;
			}
			var payload = new Dictionary<string, string> {{"key", key}, {"data", value}};
			Request.Post(Constants.ApiDatastoreSet, null, payload, response => {
				if(callback != null) {
					callback(response.Success);
				}
			}, !global);
		}

		/// <summary>
		/// Caution: Non atomic operation!
		/// Save the key/value pair in the DataStore.
		/// This method will split the payload into multiple smaller packages, in order to overcome 
		/// GameJolts 1MB limit. These packages will be appended via the update operation.
		/// If one or more parts are already uploaded and then an error occurs (for e.g. no network connection),
		/// this method will stop uploading further packages. If this happens the DataStore might contain
		/// incomplete data, therefore you might want to delete the entry or try uploading again.
		/// By using the <paramref name="progress"/> callback, you could also try to continue from the last position.
		/// </summary>
		/// <param name="key">The key name.</param>
		/// <param name="value">The value to store.</param>
		/// <param name="global">A boolean indicating whether the key is global (<c>true</c>) or private to the user (<c>false</c>).</param>
		/// <param name="callback">A callback function accepting a single parameter, a boolean indicating success.</param>
		/// <param name="progress">A callback function accepting a single parameter, the number of already uploaded bytes. 
		/// This callback is called after each successfull segment upload.</param>
		/// <param name="maxSegmentSize">Maximum segment size. The data to upload is split into segments of at most this size.</param>
		public static void SetSegmented(string key, string value, bool global, Action<bool> callback, 
			Action<int> progress = null, int maxSegmentSize = SoftLimit) {
			if(callback == null) throw new ArgumentNullException();
			if(maxSegmentSize < 10 || maxSegmentSize > SoftLimit) throw new ArgumentOutOfRangeException();
			if(key.Length + value.Length > HardLimit) {
				LogHelper.Error("Datset is larger than 16MB!");
				callback(false);
				return;
			}
			if(key.Length >= SoftLimit - 1024) {
				LogHelper.Error("Failed to upload data, because the key is too long.");
				callback(false);
				return;
			}
			const int keyLimit = SoftLimit * 3 / 4;
			if(key.Length > keyLimit) {
				LogHelper.Warning("Key is very long, only {0} bytes left for the data. " +
				                  "Therefore many segments may be needed to upload the data." +
				                  "Consider using smaller keys.", SoftLimit - key.Length);
			}

			// set first segment
			maxSegmentSize = Math.Min(maxSegmentSize, SoftLimit - key.Length);
			maxSegmentSize = Math.Min(maxSegmentSize, value.Length);
			var segmentSize = maxSegmentSize;
			var dataSend = 0;
			var segment = value.Substring(0, segmentSize);
			var payload = new Dictionary<string, string> {{"key", key}, {"data", segment}};

			Action<string> completedAction = null;
			completedAction = response => {
				if(response == null) { // request failed
					callback(false);
				} else { // request succeeded
					dataSend += segmentSize;
					if(progress != null) progress(dataSend);
					if(dataSend >= value.Length) { // data uploaded completely
						callback(true);
					} else { // append next segment
						segmentSize = Math.Min(maxSegmentSize, value.Length - dataSend);
						segment = value.Substring(dataSend, segmentSize);
						Update(key, segment, DataStoreOperation.Append, global, completedAction);
					}
				}
			};

			Request.Post(Constants.ApiDatastoreSet, null, payload, 
				response => completedAction(response.Success ? segment : null), !global);
		}

		/// <summary>
		/// Update the value for a given key in the DataStore.
		/// </summary>
		/// <param name="key">The key name.</param>
		/// <param name="value">The new value to operate with.</param>
		/// <param name="operation">The <see cref="DataStoreOperation"/> to perform.</param>
		/// <param name="global">A boolean indicating whether the key is global (<c>true</c>) or private to the user (<c>false</c>).</param>
		/// <param name="callback">A callback function accepting a single parameter, a the updated key value.</param>
		public static void Update(string key, string value, DataStoreOperation operation, bool global,
			Action<string> callback = null) {
			var parameters = new Dictionary<string, string> {{"key", key}, {"operation", operation.ToString().ToLower()}};
			var payload = new Dictionary<string, string> {{"value", value}};

			Request.Post(Constants.ApiDatastoreUpdate, parameters, payload, response => {
				if(callback != null) {
					callback(response.Success ? response.Dump : null);
				}
			}, !global, ResponseFormat.Dump);
		}

		/// <summary>
		/// Get the value for a given key from the DataStore.
		/// </summary>
		/// <param name="key">The key name.</param>
		/// <param name="global">A boolean indicating whether the key is global (<c>true</c>) or private to the user (<c>false</c>).</param>
		/// <param name="callback">A callback function accepting a single parameter, the key value.</param>
		public static void Get(string key, bool global, Action<string> callback) {
			var parameters = new Dictionary<string, string> {{"key", key}};

			Request.Get(Constants.ApiDatastoreFetch, parameters, response => {
				var value = response.Success ? response.Dump : null;
				if(callback != null) {
					callback(value);
				}
			}, !global, ResponseFormat.Dump);
		}

		/// <summary>
		/// Delete the key from the DataStore.
		/// </summary>
		/// <param name="key">The key name.</param>
		/// <param name="global">A boolean indicating whether the key is global (<c>true</c>) or private to the user (<c>false</c>).</param>
		/// <param name="callback">A callback function accepting a single parameter, a boolean indicating success.</param>
		public static void Delete(string key, bool global, Action<bool> callback = null) {
			var parameters = new Dictionary<string, string> {{"key", key}};

			Request.Get(Constants.ApiDatastoreRemove, parameters, response => {
				if(callback != null) {
					callback(response.Success);
				}
			}, !global);
		}

		/// <summary>
		/// Gets the list of available keys in the DataStore.
		/// </summary>
		/// <param name="global">A boolean indicating whether the keys are global (<c>true</c>) or private to the user (<c>false</c>).</param>
		/// <param name="callback">A callback function accepting a single parameter, a list of key names.</param>
		public static void GetKeys(bool global, Action<string[]> callback) {
			GetKeys(global, null, callback);
		}

		/// <summary>
		/// Gets the list of available keys in the DataStore.
		/// </summary>
		/// <param name="global">A boolean indicating whether the keys are global (<c>true</c>) or private to the user (<c>false</c>).</param>
		/// <param name="pattern">Only keys matching this pattern will be returned. Placeholder character is *</param>
		/// <param name="callback">A callback function accepting a single parameter, a list of key names.</param>
		public static void GetKeys(bool global, string pattern, Action<string[]> callback) {
			var parameters = new Dictionary<string, string>();
			if(!string.IsNullOrEmpty(pattern))
				parameters.Add("pattern", pattern);
			Request.Get(Constants.ApiDatastoreKeysFetch, parameters, response => {
				string[] keys;
				if(response.Success) {
					int count = response.Json["keys"].AsArray.Count;
					keys = new string[count];

					for(int i = 0; i < count; ++i) {
						keys[i] = response.Json["keys"][i]["key"].Value;
					}
				} else {
					keys = null;
				}

				if(callback != null) {
					callback(keys);
				}
			}, !global);
		}
	}
}