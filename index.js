const { DerivativesApi } = require('forge-apis');
const { getClient, getInternalToken } = require('./oauth');
const path = require('path');
const mkdirp = require('mkdirp');
const fs = require('fs');

(async () => {
    try {
        function openWriteStream(outFile) {
            let wstream = null;
            if (outFile) {
                try {
                    mkdirp.sync(path.dirname(outFile));
                    wstream = fs.createWriteStream(outFile);
                } catch (e) {
                    console.error('Error:', e.message);
                }
            }
            return wstream;
        }

        async function getItem(urn, derivativeUrn, outFile, oauth2Client, credentials) {
            try {
                const res = await derivativesApi.getDerivativeManifest(urn, derivativeUrn, {}, oauth2Client, credentials)
                if (res.statusCode !== 200)
                    return (callback(res.statusCode));
                // Skip unzipping of items to make the downloaded content compatible with viewer debugging
                let wstream = openWriteStream(outFile);
                if (wstream) {
                    wstream.write(typeof res.body === 'object' && path.extname(outFile) === '.json' ? JSON.stringify(res.body) : res.body);
                    wstream.end();

                    return true;
                }
            } catch (e) {
                console.error('Error:', e, JSON.stringify(e));
            }

            return false;
        }

        const derivativesApi = new DerivativesApi();
        const urn = 'dXJuOmFkc2sud2lTGHJZGZDpmcy5maWxlOnZmLkxiQndYWDhJUU0yLVc4bnRTdHRDR0E_dmVyc2lvbj0x';

        const token = await getInternalToken();
        const oauth2client = getClient();

        const res = await derivativesApi.getManifest(urn, {}, oauth2client, token);
        const { derivatives } = res.body;
        const viewables = derivatives[0].children;

        const pages = [];
        for (let i = 0; i < viewables.length; i++) {
            const viewable = viewables[i];
            const pageName = viewable.name;
            const pageFile = viewable.children.find(child => child.role == 'pdf-page' && child.type == 'resource');
            const thumbnails = viewable.children.filter(child => child.role == 'thumbnail' && child.type == 'resource');

            pages.push({
                name: pageName,
                file: pageFile,
                thumbnails
            })
        }

        console.log(pages);

        if (!pages || pages.length <= 0)
            throw 'Nothing to download';

        // Download folder
        const savePath = path.join(__dirname, 'download', urn);
        mkdirp.sync(savePath);

        for (let i = 0; i < pages.length; i++) {
            try {
                const page = pages[i];
                const fileResult = await getItem(
                    urn,
                    page.file.urn,
                    path.join(savePath, page.file.urn.split(urn)[1]),
                    oauth2client,
                    token
                );

                if (!fileResult)
                    throw `Failed to download the PDF file for page \`${page.name}\``;

                for (let j = 0; j < page.thumbnails.length; j++) {
                    try {
                        const thumbnail = page.thumbnails[j];
                        const thumbnailResult = await getItem(
                            urn,
                            thumbnail.urn,
                            path.join(savePath, thumbnail.urn.split(urn)[1]),
                            oauth2client,
                            token
                        );
        
                        if (!thumbnailResult)
                            throw `Failed to download the thumbnail \`${thumbnail.resolution.join('x')}\` for page \`${page.name}\``;

                    } catch (e) {
                        console.error(e);
                    }
                }

            } catch (e) {
                console.error(e);
            }
        }
    } catch (e) {
        console.error(e);
    }
})();