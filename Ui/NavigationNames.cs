using System.Collections.Generic;
namespace Sidereal.Ui;

// Public display names from browser solar-system.json r2. Physical fields always come from authorized views.
internal static class NavigationNames
{
    public static readonly IReadOnlyDictionary<string,string> Values = new Dictionary<string,string>
    {
        ["7a99b4e9-c8b8-5bae-8626-9fe2c7f2ee35"] = "Helion",
        ["6e1c58ab-aef5-5df9-b3ec-40775cf582ac"] = "Cinder",
        ["30061a60-06fc-5d89-985b-622cf95891c2"] = "Cinder I",
        ["e5644b16-4c38-545b-9be2-388ad9afcd38"] = "Cinder II",
        ["5a626553-f791-50dc-b8d1-46c8ec2f552f"] = "Basalt",
        ["d00e938a-29ab-51b7-962b-48acd827ced6"] = "Basalt I",
        ["f715117d-0d1a-5e6b-b5d2-777eeb545b68"] = "Basalt II",
        ["04482c63-55de-5807-b5de-14a7917ccd8e"] = "Dunes",
        ["830d3aeb-7565-5eaf-9c03-bcfd23c584e3"] = "Dunes I",
        ["535b72e1-1f83-5373-8654-168a653d9558"] = "Dunes II",
        ["2193aac5-5da2-5ba6-86a4-42d4dc308b48"] = "Pelagic",
        ["f4cdcce4-231b-569d-b4f0-3ce53848aa5f"] = "Pelagic I",
        ["f23b5e08-d802-5548-a8aa-ac57e10b425a"] = "Pelagic II",
        ["181bead1-f8a9-58b5-83a5-14985b64bd20"] = "Azure",
        ["0abe3b56-1574-5efa-ac64-8e46e9171a87"] = "Azure I",
        ["deb62ed7-b3d3-545d-bf11-ab5296816dce"] = "Azure II",
        ["e4b37879-64ef-5e35-9e1e-69ebd3c1b792"] = "Amethyst",
        ["1ee700fd-d300-5f07-994e-39e8542a5ffe"] = "Amethyst I",
        ["b094dbc5-38d9-5bf1-82e6-6d4c8081ae36"] = "Amethyst II",
        ["f79d3361-1fc3-54b9-90ae-be1bb027486c"] = "Amethyst III",
        ["fa8106a1-5917-5189-8f43-3b9ae57366b9"] = "Prism",
        ["83ebab23-3a86-5d27-a0ae-d0359c3a2684"] = "Prism I",
        ["6b8916ca-8ae6-5856-8aba-05d5e4f655d4"] = "Prism II",
        ["7e7b4c4f-7cbf-535f-b71f-676c0eb089b4"] = "Viridian",
        ["47705f74-5cf1-5dd9-a516-7e71346ac1d5"] = "Viridian I",
        ["785e893b-2be8-5cb4-ad24-9886c4044550"] = "Viridian II",
        ["68c229bd-ca74-5d63-b6ed-8146635647e0"] = "Frost",
        ["28d33f21-7894-5fe6-8037-22a39c152415"] = "Frost I",
        ["7320d8e3-fd3d-5ffb-85ca-2d5351f08fad"] = "Frost II",
    };
}
