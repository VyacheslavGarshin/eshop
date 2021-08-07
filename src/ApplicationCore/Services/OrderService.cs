using Ardalis.GuardClauses;
using Azure.Core;
using Azure.Storage.Queues;
using Microsoft.eShopWeb.ApplicationCore.Entities;
using Microsoft.eShopWeb.ApplicationCore.Entities.BasketAggregate;
using Microsoft.eShopWeb.ApplicationCore.Entities.OrderAggregate;
using Microsoft.eShopWeb.ApplicationCore.Interfaces;
using Microsoft.eShopWeb.ApplicationCore.Specifications;
using Newtonsoft.Json;
using System;
using System.Linq;
using System.Net;
using System.Threading.Tasks;

namespace Microsoft.eShopWeb.ApplicationCore.Services
{
    public class OrderService : IOrderService
    {
        private readonly IAsyncRepository<Order> _orderRepository;
        private readonly IUriComposer _uriComposer;
        private readonly IAsyncRepository<Basket> _basketRepository;
        private readonly IAsyncRepository<CatalogItem> _itemRepository;

        public OrderService(IAsyncRepository<Basket> basketRepository,
            IAsyncRepository<CatalogItem> itemRepository,
            IAsyncRepository<Order> orderRepository,
            IUriComposer uriComposer)
        {
            _orderRepository = orderRepository;
            _uriComposer = uriComposer;
            _basketRepository = basketRepository;
            _itemRepository = itemRepository;
        }

        public async Task CreateOrderAsync(int basketId, Address shippingAddress)
        {
            var basketSpec = new BasketWithItemsSpecification(basketId);
            var basket = await _basketRepository.FirstOrDefaultAsync(basketSpec);

            Guard.Against.NullBasket(basketId, basket);
            Guard.Against.EmptyBasketOnCheckout(basket.Items);

            var catalogItemsSpecification = new CatalogItemsSpecification(basket.Items.Select(item => item.CatalogItemId).ToArray());
            var catalogItems = await _itemRepository.ListAsync(catalogItemsSpecification);

            var items = basket.Items.Select(basketItem =>
            {
                var catalogItem = catalogItems.First(c => c.Id == basketItem.CatalogItemId);
                var itemOrdered = new CatalogItemOrdered(catalogItem.Id, catalogItem.Name, _uriComposer.ComposePicUri(catalogItem.PictureUri));
                var orderItem = new OrderItem(itemOrdered, basketItem.UnitPrice, basketItem.Quantity);
                return orderItem;
            }).ToList();

            var order = new Order(basket.BuyerId, shippingAddress, items);

            await _orderRepository.AddAsync(order);

            using (var wc = new WebClient())
            {
                await wc.UploadStringTaskAsync(new System.Uri("https://eshoponwebfunctionseuslava.azurewebsites.net/api/StoreOrder?name=order" + order.Id), JsonConvert.SerializeObject(order));

                try
                {
                    var storeOrder = new
                    {
                        order.Id,
                        ShipToAddress = JsonConvert.SerializeObject(order.ShipToAddress),
                        Total = order.Total(),
                        Items = JsonConvert.SerializeObject(order.OrderItems)
                    };
                    await wc.UploadStringTaskAsync(new System.Uri("https://eshoponwebfunctionseuslava.azurewebsites.net/api/StoreOrderToCosmosDb"),
                        JsonConvert.SerializeObject(storeOrder));
                }
                catch (Exception ex)
                {
                    Console.WriteLine("Error calling StoreOrderToCosmosDb" + ex.GetBaseException().Message);
                }

                var options = new QueueClientOptions
                {
                    MessageEncoding = QueueMessageEncoding.Base64,
                };
                options.Retry.Mode = RetryMode.Fixed;
                options.Retry.MaxRetries = 3;
                options.Retry.Delay = new TimeSpan(0, 0, 5);

                var queuClient = new QueueClient("DefaultEndpointsProtocol=https;AccountName=mainslava;AccountKey=ZkCubOE+Yt9xnIGlXzMDqR6vFro05ciuI0mfr0Y48Rtgk1XkxmYvVVUOhY2OoMeriU9TgrEoh0XqvmiuXLxPxQ==;EndpointSuffix=core.windows.net"
                    , "eshop-orders", options);
                await queuClient.CreateIfNotExistsAsync();
                await queuClient.SendMessageAsync(JsonConvert.SerializeObject(new
                {
                    OrderId = order.Id,
                    OrderJson = JsonConvert.SerializeObject(order),
                }));
            }
        }
    }
}
