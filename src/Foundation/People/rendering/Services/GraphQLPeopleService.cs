﻿using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Mvp.Foundation.People.Infrastructure;
using Newtonsoft.Json;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Mvp.Foundation.People.Services
{
	public class GraphQLPeopleService : IGraphQLPeopleService
	{
		private readonly IMemoryCache _memoryCache;
		private readonly IConfiguration _configuration;
		private readonly IGraphQLProvider _graphQLProvider;


		public GraphQLPeopleService(IMemoryCache memoryCache, IConfiguration configuration, IGraphQLProvider graphQLProvider)
		{
			_memoryCache = memoryCache;
			_configuration = configuration;
			_graphQLProvider = graphQLProvider;
		}

		public SearchParams CreateSearchParams()
		{
			return new SearchParams()
			{
				FacetOn = new List<string>() { "personaward", "personyear" }
			};

		}


		public async Task<PeopleSearchResults> Search(SearchParams searchParams)
		{
			
			return await ProductSearchResults(searchParams, true);

		}

		async Task<PeopleSearchResults> ProductSearchResults(SearchParams searchParams, bool correctFacets)
		{
			var fieldsEqualsList = new List<FieldFilter>();


			//Facets from URL
			if (searchParams.Facets != null && searchParams.Facets.Any())
				fieldsEqualsList.AddRange(AddFacetFilters(searchParams.Facets));

			List<FieldFilter> fieldFilters = new List<FieldFilter>();
			fieldFilters.Add(new FieldFilter() { name = "_templatename", value = "Person" });
			fieldFilters.Add(new FieldFilter() { name = "ismvp", value = "true" });
			fieldsEqualsList.AddRange(fieldFilters);
			GraphQL.GraphQLResponse<Response> response = await _graphQLProvider.SendQueryAsync<Response>(searchParams.IsInEditingMode, GraphQLFiles.PeopleSearchAdvanced, new
			{
				language = searchParams.Language,
				rootItem = new Guid(searchParams.RootItemId).ToString("N"),
				pageSize = searchParams.PageSize,
				cursorValueToGetItemsAfter = searchParams.CursorValueToGetItemsAfter?.ToString(),
				query = searchParams.Query,
				fieldsEqual = fieldsEqualsList,
				facetOn = searchParams.FacetOn
			});
			if (searchParams.Facets != null && searchParams.Facets.Any())
			{
				response.Data.Search.facets = response.Data.Search.facets.Select(fc => UpdateFacetState(fc, searchParams.Facets)).ToList();
			}
			var result = new PeopleSearchResults
			{
				People = response.Data.Search.results.items.Select(x => x.item),
				Facets = response.Data.Search.facets,
				TotalCount = response.Data.Search.results.totalCount,
				StartCursor = int.Parse(response.Data.Search.results.pageInfo.startCursor),
				EndCursor = int.Parse(response.Data.Search.results.pageInfo.endCursor),
				HasNextPage = response.Data.Search.results.pageInfo.hasNextPage,
				HasPreviousPage = response.Data.Search.results.pageInfo.hasPreviousPage,
				PageSize = searchParams.PageSize != 0 ? searchParams.PageSize : null,
				FilterFacets = searchParams.FilterFacets,
				keyword = searchParams.Query,
				CurrentPage = !searchParams.PageSize.HasValue
					? 0
					: Convert.ToInt32(Math.Ceiling(int.Parse(response.Data.Search.results.pageInfo.endCursor) /
												   Convert.ToDouble(searchParams.PageSize)))
			};
			if(correctFacets)
			{
				result =  await CorrectFacetCounts(searchParams, result);
				if (searchParams.Facets != null && searchParams.Facets.Any())
				{
					result.Facets = result.Facets.Select(fc => UpdateFacetState(fc, searchParams.Facets)).ToList();
				}
			}
			result.Facets = result.Facets.Select(fc => UpdateFacetValuesOrder(fc)).ToList();
			return result;
		}

		private List<FieldFilter> AddFacetFilters(List<KeyValuePair<string, string[]>> facetFilters)
		{
			List<FieldFilter> fieldFilters = new List<FieldFilter>();
			foreach (KeyValuePair<string, string[]> filter in facetFilters)
			{
				foreach( string filterValue in filter.Value)
				{
					FieldFilter ff = new FieldFilter();
					ff.name = filter.Key;
					ff.value = filterValue;
					fieldFilters.Add(ff);
				}
			}
			return fieldFilters;
		}

		private Facet UpdateFacetState(Facet facet, IList<KeyValuePair<string, string[]>> checkedFacets)
		{
			foreach (KeyValuePair<string, string[]> checkedFacet in checkedFacets)
			{
				if (checkedFacet.Key.Equals(facet.name) && checkedFacet.Value != null && checkedFacet.Value.Any())
				{
					foreach (var facetValue in facet.values)
					{
						facetValue.isChecked = checkedFacet.Value.Contains(facetValue.value);
					}
				}
			}
			return facet;
		}

		private Facet UpdateFacetValuesOrder(Facet facet)

		{
			if (facet.name == "personaward")
			{
				facet.values = facet.values.OrderByDescending(v => v.count).ToList();
				facet.DisplayName = "Type";

			}
			else if (facet.name == "personyear")
			{
				facet.values = facet.values.OrderByDescending(v => GetYearValue(v.value)).ToList();
				facet.DisplayName = "Year";
			}

			return facet;
		}

		private int GetYearValue(string yearstring)
		{
			int year = 0;
			Int32.TryParse(yearstring, out year);
			return year;

		}

		private List<FieldFilter> AddFieldsEqualParams(KeyValuePair<string, string>[] filter)
		{
			List<FieldFilter> fieldFilters = new List<FieldFilter>();

			foreach (KeyValuePair<string, string> keyValue in filter)
			{
				FieldFilter ff = new FieldFilter();
				ff.name = keyValue.Key;
				ff.value = keyValue.Value;
				fieldFilters.Add(ff);
			}
			return fieldFilters;

		}

		async Task<PeopleSearchResults> CorrectFacetCounts(SearchParams searchParams, PeopleSearchResults originalResults)
		{
			foreach (string facetOn in searchParams.FacetOn)
			{
				SearchParams temp = CopySearchParams(searchParams);
				temp.PageSize = 1;
				temp.CursorValueToGetItemsAfter = 0;
				//var otherFacetOn = temp.FacetOn.Where(fc => !fc.Equals(facetOn)).ToList();
				var facets = temp.Facets.Where(fc => !fc.Key.Equals(facetOn)).ToList();
				//temp.FacetOn = otherFacetOn;
				temp.Facets = facets;

				var searchResultsTemp = await ProductSearchResults(temp, false);

				var facetToremove = originalResults.Facets.Where(fct => fct.name.Equals(facetOn)).FirstOrDefault();

				var facetToadd = searchResultsTemp.Facets.Where(fct => fct.name.Equals(facetOn)).FirstOrDefault();
				if (facetToremove != null && facetToadd != null)
				{
					originalResults.Facets.Remove(facetToremove);
					originalResults.Facets.Add(facetToadd);
				}
			}
			return originalResults;
		}

		private SearchParams CopySearchParams(SearchParams searchParams)
		{
			SearchParams cloned = new SearchParams();
			cloned.CacheKey = searchParams.CacheKey;
			cloned.Language = searchParams.Language;
			cloned.RootItemId = searchParams.RootItemId;
			cloned.PageSize = searchParams.PageSize;
			cloned.CursorValueToGetItemsAfter = searchParams.CursorValueToGetItemsAfter;
			cloned.FilterFacets = searchParams.FilterFacets;
			cloned.Facets = searchParams.Facets;
			cloned.FacetOn = searchParams.FacetOn;
			cloned.Query = searchParams.Query;
			return cloned;
		}
	}

	public class SearchParams
	{
		public string Language { get; set; }
		public string RootItemId { get; set; }
		public int? PageSize { get; set; }

		public int? CursorValueToGetItemsAfter { get; set; }

		public bool? IsInEditingMode { get; set; }

		public IList<(KeyValuePair<string, string>, IDictionary<string, string>)>? FilterFacets { get; set; }
		public List<KeyValuePair<string, string[]>>? Facets { get; set; }

		public IList<string> FacetOn { get; set; }
		public string Query { get; set; }

		public string CacheKey { get; set; }
	}

	public class PeopleSearchResults
	{
		public IEnumerable<Person> People { get; set; }
		public List<Facet> Facets { get; set; }
		public int TotalCount { get; set; }
		public int StartCursor { get; set; }
		public int EndCursor { get; set; }
		public bool HasNextPage { get; set; }
		public bool HasPreviousPage { get; set; }
		public int? PageSize { get; set; }
		public IList<(KeyValuePair<string, string>, IDictionary<string, string>)>? FilterFacets { get; set; }
		public int CurrentPage { get; set; }
		public string keyword { get; set; }
	}

	public class PeopleSearch
	{
		public List<Facet> facets { get; set; }
		public Results results { get; set; }
	}

	public class Response
	{
		public PeopleSearch Search { get; set; }
	}


	public class Value
	{
		public string value { get; set; }
		public bool isChecked {get;set;}
		public int count { get; set; }
	}

	public class Facet
	{
		public string name { get; set; }
		public string DisplayName { get; set; }
		public List<Value> values { get; set; }
		
	}

	public class FirstName
	{
		public string value { get; set; }
	}

	public class LastName
	{
		public string value { get; set; }
	}

	public class Email
	{
		public string value { get; set; }
	}

	public class Introduction
	{
		public string value { get; set; }
	}

	public class TargetItem
	{
		public string name { get; set; }
	}

	public class Country
	{
		public TargetItem targetItem { get; set; }
	}

	public class Person
	{
		public FirstName firstName { get; set; }
		public LastName lastName { get; set; }
		public Email email { get; set; }
		public Introduction introduction { get; set; }
		public string url { get; set; }
		public Country country { get; set; }
		public string mvpAwards { get; set; }
	}


	public class SearchItem
	{
		public Person item { get; set; }
	}

	public class PageInfo
	{
		public string startCursor { get; set; }
		public string endCursor { get; set; }
		public bool hasNextPage { get; set; }
		public bool hasPreviousPage { get; set; }
	}

	public class Results
	{
		public List<SearchItem> items { get; set; }
		public int totalCount { get; set; }
		public PageInfo pageInfo { get; set; }
	}

	public class FieldFilter
	{
		public string name { get; set; }
		public string value { get; set; }
	}



}