FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Lumora.Auth.slnx ./
COPY src/ src/
RUN dotnet publish src/Lumora.Auth.Web/Lumora.Auth.Web.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app ./
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
VOLUME /data
ENTRYPOINT ["dotnet", "Lumora.Auth.Web.dll"]
