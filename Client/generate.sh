VERSION=0.1.1
PACKAGE_NAME=Coflnet.Sky.Indexer.Client

docker run --rm -v "${PWD}:/local" --network host -u $(id -u ${USER}):$(id -g ${USER}) openapitools/openapi-generator-cli generate \
-i http://localhost:5016/swagger/v1/swagger.json \
-g csharp \
-o /local/out --additional-properties=packageName=$PACKAGE_NAME,packageVersion=$VERSION,licenseId=MIT,targetFramework=net10.0,library=restsharp

cd out
project=src/$PACKAGE_NAME/$PACKAGE_NAME.csproj
sed -i 's/GIT_USER_ID/Coflnet/g' "$project"
sed -i 's/GIT_REPO_ID/SkyIndexer/g' "$project"
sed -i 's/>OpenAPI/>Coflnet/g' "$project"

dotnet pack
cp src/$PACKAGE_NAME/bin/Release/$PACKAGE_NAME.*.nupkg ..
dotnet nuget push ../$PACKAGE_NAME.$VERSION.nupkg --api-key $NUGET_API_KEY --source "nuget.org" --skip-duplicate
rm -r *.sln
