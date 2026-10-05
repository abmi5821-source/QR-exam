FROM mcr.microsoft.com/dotnet/sdk:8.0 AS b
WORKDIR /s
COPY . .
RUN dotnet publish -c Release -o /o
FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /a
COPY --from=b /o .
CMD ["sh","-c","ASPNETCORE_URLS=http://+:${PORT:-10000} dotnet QrExam.dll"]
