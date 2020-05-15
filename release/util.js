const cp = require('child_process');
const fs = require('fs');

const GIT = 'git';
const GIT_RELEASE_RE = /([0-9]{1,3}\.[0-9]{1,3}\.[0-9]{1,3})/;

exports.verifyMinimumNodeVersion = function()
{
    var version = process.version;
    var minimumNodeVersion = '12.10.0'; // this is the version of node that supports the recursive option to rmdir
    if (parseFloat(version.substr(1,version.length)) < parseFloat(minimumNodeVersion))
    {
        console.log('Version of Node does not support recursive directory deletes. Be sure you are starting with a clean workspace!');

    }
    console.log(`Using node version ${version}`);
}

exports.verifyMinimumGitVersion = function()
{
    var gitVersionOutput = cp.execSync(`${GIT} --version`, { encoding: 'utf-8'});
    if (!gitVersionOutput)
    {
        console.log('Unable to get Git Version.');
        process.exit(-1);
    }
    var gitVersion = gitVersionOutput.match(GIT_RELEASE_RE)[0];

    var minimumGitVersion = '2.25.0'; // this is the version that supports sparse-checkout
    if (parseFloat(gitVersion) < parseFloat(minimumGitVersion))
    {
        console.log(`Version of Git does not meet minimum requirement of ${minimumGitVersion}`);
        process.exit(-1);
    }
    console.log(`Using git version ${gitVersion}`);

}

exports.execInForeground = function(command, directory, dryrun = false)
{
    directory = directory || '.';
    console.log(`% ${command}`);
    if (!dryrun)
    {
        cp.execSync(command, { cwd: directory, stdio: [process.stdin, process.stdout, process.stderr] });
    }
}

exports.versionifySync = function(template, destination, version)
{
    try
    {
        var data = fs.readFileSync(template, 'utf8');
        data = data.replace(/<AGENT_VERSION>/g, version);
        console.log(`Generating ${destination}`);
        fs.writeFileSync(destination, data);
    }
    catch(e)
    {
        console.log('Error:', e.stack);
    }
}